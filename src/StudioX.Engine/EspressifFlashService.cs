namespace StudioX.Engine;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>使用已验收 SDK 构建凭据执行多映像下载；不运行 idf.py flash，不隐式构建。</summary>
public sealed class EspressifFlashService(ToolsetCatalog catalog,
    Func<ProcessRequest, CancellationToken, Task<ProcessResult>>? runProcess = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<EspressifFlashConfiguration?> ConfigurationAsync(string projectDirectory, CancellationToken token = default)
    {
        var project = await ProjectService.ReadAsync(projectDirectory, token);
        if (project.Espressif is null)
        {
            return null;
        }
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(projectDirectory, "device/manifest.json"), token);
        var device = pack.Devices.Single(item => item.Id == project.DeviceId);
        if (device.Espressif is not { } sdk || sdk.Target != project.Espressif.Target ||
            sdk.Framework != project.Espressif.Framework || sdk.SdkVersion != project.Espressif.SdkVersion)
        {
            throw new StudioXException("ESP_FLASH_PROJECT", "器件包与工程锁定的 SDK 或芯片不一致。");
        }
        return new(project, device, await EspressifFlashSettings.ReadAsync(projectDirectory, token));
    }

    public async Task SaveSettingsAsync(string root, EspressifFlashSettings settings, CancellationToken token = default)
    {
        settings.Validate();
        _ = await ConfigurationAsync(root, token) ?? throw Unsupported();
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, EspressifFlashSettings.RelativePath), settings, token);
    }

    /// <summary>只读预检，不创建快照、不启动下载工具，也不枚举或连接硬件。</summary>
    public async Task<EspressifFlashPreview> PreviewAsync(string root, EspressifFlashSettings settings, CancellationToken token = default)
    {
        return (await ValidateAsync(root, settings, token)).Preview;
    }

    private sealed record ValidatedFlash(EspressifFlashPreview Preview, ResolvedToolset Tools);

    private async Task<ValidatedFlash> ValidateAsync(string root, EspressifFlashSettings settings, CancellationToken token)
    {
        settings.Validate(requirePort: true);
        var configuration = await ConfigurationAsync(root, token) ?? throw Unsupported();
        var project = configuration.Project;
        var tools = await catalog.ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId, token);
        var lockPath = PathBoundary.Resolve(root, ".studiox/toolchain.lock.json");
        var receiptPath = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
        if (!File.Exists(lockPath) || !File.Exists(receiptPath) ||
            await JsonStore.ReadAsync<ToolchainLock>(lockPath, token) != new ToolchainLock(1, project.ToolsetId, project.ToolsetVersion, tools.Fingerprint))
        {
            throw new StudioXException("ESP_FLASH_BUILD", "请先使用当前内置 SDK 成功编译工程。");
        }
        var receipt = await JsonStore.ReadAsync<BuildReceipt>(receiptPath, token);
        if (receipt.Project != project || receipt.ToolFingerprint != tools.Fingerprint || receipt.EspressifLayout is not { } approved ||
            receipt.SourceStamp is null || receipt.SourceStamp != await DebugSourceStamp.ComputeAsync(root, token))
        {
            throw new StudioXException("ESP_FLASH_BUILD", "源码、SDK 配置或工具集已变化；请重新编译后下载。");
        }
        var module = await EspressifModuleConfiguration.ReadAsync(root, token);
        var layout = await EspressifFlashLayoutReader.ParseAsync(root, approved.FlasherArgumentsRelativePath,
            project.Espressif!.Target, module.DeclaredFlashBytes, token);
        await EspressifModuleSdkConfig.VerifyAsync(root, module, layout, token);
        if (layout.LayoutSha256 != approved.LayoutSha256 || receipt.Images.Length != layout.Images.Length ||
            layout.Images.Any(image => !receipt.Images.Any(built => built.RelativePath == image.RelativePath &&
                built.Format == "bin" && built.Sha256 == image.Sha256)))
        {
            throw new StudioXException("ESP_FLASH_CHANGED", "编译后的下载布局或 BIN 已被修改；请重新编译后下载。");
        }
        var esptoolVersion = tools.Manifest.ComponentVersions?.GetValueOrDefault("esptool");
        if (esptoolVersion is null || !Regex.IsMatch(esptoolVersion, @"^[2-4]\.[0-9]+", RegexOptions.CultureInvariant))
        {
            throw new StudioXException("ESP_FLASH_TOOL", "内置工具集没有锁定受支持的 esptool 版本。");
        }
        return new(new(configuration with
        {
            Settings = settings
        }, layout, esptoolVersion), tools);
    }

    /// <summary>授权值覆盖整个布局；审批后再核对所有产物，然后只复制 BIN 到本次下载快照。</summary>
    public async Task<EspressifFlashReport> DownloadApprovedAsync(string projectDirectory, EspressifFlashSettings settings,
        string expectedDeviceId, string expectedLayoutSha256, IProgress<string>? output = null, CancellationToken token = default)
    {
        settings.Validate(requirePort: true);
        if (string.IsNullOrWhiteSpace(expectedDeviceId) || string.IsNullOrWhiteSpace(expectedLayoutSha256) ||
            expectedLayoutSha256.Length != 64 || !expectedLayoutSha256.All(Uri.IsHexDigit))
        {
            throw new StudioXException("ESP_FLASH_APPROVAL", "下载需要预检返回的准确器件与完整布局 SHA-256。");
        }
        if (!await gate.WaitAsync(0, token))
        {
            throw new StudioXException("ESP_FLASH_BUSY", "ESP 下载工具正在使用中。");
        }
        try
        {
            var root = Path.GetFullPath(projectDirectory);
            var validated = await ValidateAsync(root, settings, token);
            var layout = validated.Preview.Layout;
            if (validated.Preview.Configuration.Device.Id != expectedDeviceId ||
                !layout.LayoutSha256.Equals(expectedLayoutSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("ESP_FLASH_APPROVAL_CHANGED", "审批后器件或下载布局发生变化；未连接硬件。");
            }
            var session = PathBoundary.Resolve(root, ".build/esp-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            var logPath = Path.Combine(session, "esptool.log");
            var log = new FlashOutput(output);
            var snapshots = new List<string>();
            try
            {
                foreach (var image in layout.Images)
                {
                    var bytes = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, image.RelativePath), token);
                    if (bytes.LongLength != image.Bytes || Convert.ToHexString(SHA256.HashData(bytes)) != image.Sha256)
                    {
                        throw new StudioXException("ESP_FLASH_APPROVAL_CHANGED", "创建快照时 BIN 发生变化；未连接硬件。");
                    }
                    var snapshot = Path.Combine(session, "image-" + image.Offset.ToString("x8", CultureInfo.InvariantCulture) + ".bin");
                    snapshots.Add(snapshot);
                    await File.WriteAllBytesAsync(snapshot, bytes, token);
                }
                log.Report($"目标 {layout.Target} / {expectedDeviceId}，端口 {settings.Port} / {settings.BaudRate} baud\n" +
                    $"布局 SHA-256：{layout.LayoutSha256}\nFlash：{layout.FlashSize} / {layout.FlashMode} / {layout.FlashFrequency}\n");
                var environment = ToolsetEnvironment.Create(validated.Tools);
                environment["PYTHONHOME"] = validated.Tools.ResourceDirectory("python-env");
                environment["PYTHONUNBUFFERED"] = "1";
                var versionMajor = int.Parse(validated.Preview.EsptoolVersion.AsSpan(0, 1), CultureInfo.InvariantCulture);
                var runner = runProcess ?? new ProcessRunner().RunAsync;
                var write = await runner(new(validated.Tools.Tool("python"), CreateArguments(layout, settings, snapshots, verify: false, versionMajor),
                    session, TimeSpan.FromMinutes(5), environment, RemoveEnvironment: RemovedEnvironment, Output: log), token);
                log.Report($"\nwrite exit={write.ExitCode}, timeout={write.TimedOut}, truncated={write.OutputTruncated}\n");
                if (!write.Success || write.OutputTruncated)
                {
                    return new(false, log.Text, logPath, write.ExitCode, write.TimedOut);
                }
                // 写入成功后独立检查每个映像；不能用进程退出码替代 Flash 校验证据。
                var verify = await runner(new(validated.Tools.Tool("python"), CreateArguments(layout, settings, snapshots, verify: true, versionMajor),
                    session, TimeSpan.FromMinutes(5), environment, RemoveEnvironment: RemovedEnvironment, Output: log), token);
                log.Report($"\nverify exit={verify.ExitCode}, timeout={verify.TimedOut}, truncated={verify.OutputTruncated}\n");
                var success = verify.Success && !verify.OutputTruncated && HasVerificationEvidence(verify.StandardOutput, layout, versionMajor);
                if (!success && verify.Success)
                {
                    log.Report("校验输出没有覆盖全部映像，不能确认下载成功。\n");
                }
                return new(success, log.Text, logPath, verify.ExitCode, verify.TimedOut);
            }
            catch (Exception ex)
            {
                log.Report("\n" + ex + "\n下载未确认完成。\n");
                throw;
            }
            finally
            {
                try
                {
                    await File.WriteAllTextAsync(logPath, log.Text, CancellationToken.None);
                }
                finally
                {
                    // 日志落盘失败时仍清理临时映像，不积累固件历史副本。
                    foreach (var snapshot in snapshots)
                    {
                        File.Delete(snapshot);
                    }
                }
            }
        }
        finally { gate.Release(); }
    }

    public static string[] CreateArguments(EspressifFlashLayout layout, EspressifFlashSettings settings,
        IReadOnlyList<string> snapshots, bool verify, int esptoolMajor = 4)
    {
        settings.Validate(requirePort: true);
        if (snapshots.Count != layout.Images.Length || layout.Images.Length == 0 || esptoolMajor is < 2 or > 4)
        {
            throw new StudioXException("ESP_FLASH_ARGUMENTS", "下载快照或工具版本不匹配。");
        }
        List<string> arguments = ["-s", "-B", "-m", "esptool", "--chip", layout.Target, "--port", settings.Port,
            "--baud", settings.BaudRate.ToString(CultureInfo.InvariantCulture), "--before", "default_reset",
            "--after", "hard_reset"];
        if (!layout.UseStub)
        {
            arguments.Add("--no-stub");
        }
        arguments.AddRange([verify ? "verify_flash" : "write_flash", "--flash_mode", layout.FlashMode,
            "--flash_size", layout.FlashSize, "--flash_freq", layout.FlashFrequency]);
        for (var index = 0; index < layout.Images.Length; index++)
        {
            arguments.Add("0x" + layout.Images[index].Offset.ToString("x", CultureInfo.InvariantCulture));
            arguments.Add(snapshots[index]);
        }
        return arguments.ToArray();
    }

    public static bool HasVerificationEvidence(string output, EspressifFlashLayout layout, int esptoolMajor = 4)
    {
        if (esptoolMajor is < 2 or > 4)
        {
            return false;
        }
        var pending = new HashSet<uint>(layout.Images.Select(image => image.Offset));
        uint? current = null;
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"Verifying 0x[0-9a-f]+ \(([0-9]+)\) bytes @ 0x([0-9a-f]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (line.TrimStart().StartsWith("Verifying ", StringComparison.OrdinalIgnoreCase))
            {
                current = null;
                if (match.Success && uint.TryParse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) &&
                    long.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var count) &&
                    layout.Images.Any(image => image.Offset == address && ((image.Bytes + 3) & ~3L) == count))
                {
                    current = address;
                }
            }
            else if (line.Contains("-- verify OK (digest matched)", StringComparison.Ordinal) && current is { } verified)
            {
                pending.Remove(verified);
                current = null;
            }
            else if (line.Contains("verify FAILED", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return layout.Images.Length > 0 && pending.Count == 0;
    }

    private static string[] RemovedEnvironment => ToolsetEnvironment.AmbientVariables.Concat(
        new[] { "ESPTOOL_CHIP", "ESPTOOL_PORT", "ESPTOOL_BAUD", "ESPTOOL_BEFORE", "ESPTOOL_AFTER", "ESPTOOL_STUB_VERSION", "ESPTOOL_CFGFILE" }).ToArray();

    private static StudioXException Unsupported() => new("ESP_FLASH_UNSUPPORTED", "当前工程不是锁定 SDK 的 Espressif 原生工程。");

    private sealed class FlashOutput(IProgress<string>? output) : IProgress<string>
    {
        private readonly StringBuilder text = new();
        public string Text
        {
            get
            {
                lock (text)
                {
                    return text.ToString();
                }
            }
        }
        public void Report(string value)
        {
            lock (text)
            {
                var available = Math.Max(0, 5 * 1024 * 1024 - text.Length);
                text.Append(value.AsSpan(0, Math.Min(value.Length, available)));
            }
            output?.Report(value);
        }
    }
}
