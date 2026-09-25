namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record StcIspToolStatus(bool Available, string? ProgrammerExecutable, string? PythonExecutable, string Message);
public sealed record StcIspPreview(string ProjectRoot, string ExpectedModel, uint ExpectedCodeBytes,
    string Port, string SourceImage, string ImageSha256, int ImageBytes, int DataBytes,
    int HighestAddress, StcIspSettings Settings, StcIspToolStatus Tool);
public sealed record StcIspPreparation(string ProjectRoot, string ExpectedModel, uint ExpectedCodeBytes, string Port, string SourceImage, string Image,
    string ImageSha256, int DataBytes, int HighestAddress, string PythonExecutable, string GuardScript, string LogPath,
    StcIspSettings Settings);
public sealed record StcIspReport(bool Success, string Log, string LogPath, int ExitCode, bool TimedOut, bool ModelVerified)
{
    public string Summary => Success
        ? "STC ISP 报告写入完成并已退出；此工具不执行 Flash 读回校验，请观察板上程序运行。"
        : TimedOut ? "STC ISP 等待或写入超时；请查看日志确认芯片状态。" :
            "STC ISP 未完成；若已开始擦写，板内程序可能不完整，请查看原始日志。";
}

/// <summary>外部 stcgal 1.10 的受控串口下载；不把本机 Python 路径写进工程或工具集。</summary>
public sealed class StcIspService(ToolsetCatalog catalog, string runtimeDirectory, string dataDirectory)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string runtime = Path.GetFullPath(runtimeDirectory);
    private readonly string data = Path.GetFullPath(dataDirectory);
    private const string ToolSettingsName = "stc-isp-tool.json";
    private sealed record ToolSettings(string ProgrammerExecutable);
    private sealed record PreviewInputs(StcIspPreview Preview, byte[] Image);

    public Task<StcIspSettings> LoadSettingsAsync(string root, CancellationToken token = default) => StcIspSettings.ReadAsync(root, token);

    public async Task SaveSettingsAsync(string root, StcIspSettings settings, CancellationToken token = default)
    {
        var capabilities = await GetCapabilitiesAsync(root, token);
        settings.ValidateFor(capabilities);
        var previous = await StcIspSettings.ReadAsync(root, token);
        if (previous == settings) return;
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, StcIspSettings.RelativePath), settings, token);
        if (previous.ClockMode != settings.ClockMode || previous.ClockFrequencyHz != settings.ClockFrequencyHz)
        {
            var receipt = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
            if (File.Exists(receipt)) File.Delete(receipt);
        }
    }

    public async Task<StcIspCapabilities> GetCapabilitiesAsync(string root, CancellationToken token = default)
    {
        var (_, device) = await ReadProjectDeviceAsync(root, token);
        return StcIspCapabilities.For(device);
    }

    public async Task SaveProgrammerPathAsync(string programmerExecutable, CancellationToken token = default)
    {
        var status = await InspectToolAsync(programmerExecutable, token);
        if (!status.Available) throw new StudioXException("STC_ISP_TOOL", status.Message);
        await JsonStore.WriteAsync(Path.Combine(data, ToolSettingsName), new ToolSettings(status.ProgrammerExecutable!), token);
    }

    /// <summary>仅检查可用性，不打开 COM 端口。</summary>
    public async Task<StcIspToolStatus> GetToolStatusAsync(CancellationToken token = default)
    {
        var settingPath = Path.Combine(data, ToolSettingsName);
        string? previousFailure = null;
        if (File.Exists(settingPath))
        {
            var configured = await JsonStore.ReadAsync<ToolSettings>(settingPath, token);
            var selected = await InspectToolAsync(configured.ProgrammerExecutable, token);
            if (selected.Available) return selected;
            previousFailure = "所选 stcgal 已不可用：" + selected.Message;
        }
        var candidate = Path.Combine(runtime, "stc-isp", "Scripts", "stcgal.exe");
        if (File.Exists(candidate))
        {
            var bundled = await InspectToolAsync(candidate, token);
            if (bundled.Available) return bundled with { Message = previousFailure is null ? bundled.Message : previousFailure + "；已回退到发行版自带运行时。" };
            previousFailure = (previousFailure is null ? "" : previousFailure + "；") + bundled.Message;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = directory.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            candidate = Path.Combine(path, "stcgal.exe");
            if (!File.Exists(candidate)) continue;
            var discovered = await InspectToolAsync(candidate, token);
            if (discovered.Available) return discovered with { Message = previousFailure is null ? discovered.Message : previousFailure + "；已回退到 PATH 中的可用运行时。" };
            previousFailure = (previousFailure is null ? "" : previousFailure + "；") + discovered.Message;
        }
        return new(false, null, null, (previousFailure is null ? "" : previousFailure + "；") +
            "未找到可用的 stcgal.exe；请安装 stcgal 1.10，或选择已安装的 stcgal.exe。此检查不访问开发板。");
    }

    private static async Task<StcIspToolStatus> InspectToolAsync(string? programmerExecutable, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(programmerExecutable) || !Path.IsPathFullyQualified(programmerExecutable) ||
            !string.Equals(Path.GetFileName(programmerExecutable), "stcgal.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(programmerExecutable))
            return new(false, programmerExecutable, null, "所选文件不是现有的 stcgal.exe。");
        var programmer = Path.GetFullPath(programmerExecutable);
        var scripts = Path.GetDirectoryName(programmer)!;
        var candidates = new[] { Path.Combine(scripts, "python.exe"), Path.Combine(Path.GetDirectoryName(scripts)!, "python.exe") };
        var python = candidates.FirstOrDefault(File.Exists);
        if (python is null) return new(false, programmer, null, "未找到与 stcgal.exe 同环境的 python.exe；请选择完整安装的 stcgal 环境。");
        try
        {
            // 同一个 Python 环境必须提供 stcgal 1.10、pyserial 和 tqdm。只导入模块，不连接串口。
            var result = await new ProcessRunner().RunAsync(new(python,
                ["-B", "-c", "import stcgal,serial,tqdm; print('STUDIOX_STCGAL=' + stcgal.__version__)"],
                scripts, TimeSpan.FromSeconds(15), RemoveEnvironment: ["PYTHONHOME", "PYTHONPATH"]), token);
            if (!result.Success || !result.StandardOutput.Contains("STUDIOX_STCGAL=1.10", StringComparison.Ordinal))
                return new(false, programmer, python, "所选环境未提供 stcgal 1.10、pyserial 或 tqdm：" + (result.StandardError + result.StandardOutput).Trim());
            return new(true, programmer, python, "stcgal 1.10 可用（MIT 许可）。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, programmer, python, "无法检查 stcgal：" + ex.Message);
        }
    }

    /// <summary>只读预检型号、构建凭据、HEX 与工具；不创建下载快照，也不访问 COM 口。</summary>
    public async Task<StcIspPreview> PreviewAsync(string root, StcIspSettings settings,
        CancellationToken token = default) =>
        (await PreviewCoreAsync(root, settings, token).ConfigureAwait(false)).Preview;

    /// <summary>离线核对型号、构建凭据、固件边界并创建下载快照；不会访问 COM 口。</summary>
    public Task<StcIspPreparation> PrepareAsync(string root, StcIspSettings settings, CancellationToken token = default)
        => Task.Run(() => PrepareCoreAsync(root, settings, token), token);

    private async Task<StcIspPreparation> PrepareCoreAsync(string directory, StcIspSettings settings, CancellationToken token)
    {
        var inputs = await PreviewCoreAsync(directory, settings, token).ConfigureAwait(false);
        var preview = inputs.Preview;
        var root = preview.ProjectRoot;
        var session = PathBoundary.Resolve(root, ".build/stc-isp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var image = Path.Combine(session, "firmware.hex");
        await File.WriteAllBytesAsync(image, inputs.Image, token);
        var script = Path.Combine(session, "studiox-stcgal-guard.py");
        await using (var resource = typeof(StcIspService).Assembly.GetManifestResourceStream("StudioX.Engine.Resources.studiox-stcgal-guard.py")
            ?? throw new StudioXException("STC_ISP_TOOL", "STC ISP 型号防护脚本缺失。"))
        await using (var destination = File.Create(script)) await resource.CopyToAsync(destination, token);
        return new(root, preview.ExpectedModel, preview.ExpectedCodeBytes, preview.Port, preview.SourceImage, image,
            preview.ImageSha256, preview.DataBytes, preview.HighestAddress,
            preview.Tool.PythonExecutable!, script, Path.Combine(session, "stcgal.log"), settings);
    }

    private async Task<PreviewInputs> PreviewCoreAsync(string directory, StcIspSettings settings, CancellationToken token)
    {
        var root = Path.GetFullPath(directory);
        var (project, device) = await ReadProjectDeviceAsync(root, token);
        settings.ValidateFor(StcIspCapabilities.For(device), requirePort: true);
        var saved = await StcIspSettings.ReadAsync(root, token);
        if (saved != settings)
            throw new StudioXException("STC_ISP_BUILD", "STC ISP 设置尚未按工程保存，请先保存；时钟设置变化后还需重新编译。");
        var tools = await catalog.ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId, token);
        var lockPath = PathBoundary.Resolve(root, ".studiox/toolchain.lock.json");
        var receiptPath = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
        if (!File.Exists(lockPath) || await JsonStore.ReadAsync<ToolchainLock>(lockPath, token) !=
            new ToolchainLock(1, project.ToolsetId, project.ToolsetVersion, tools.Fingerprint) || !File.Exists(receiptPath))
            throw new StudioXException("STC_ISP_BUILD", "请先使用当前 STC 工具链成功编译工程。");
        var receipt = await JsonStore.ReadAsync<BuildReceipt>(receiptPath, token);
        if (receipt.Project != project || receipt.ToolFingerprint != tools.Fingerprint || receipt.Images.Length != 1 ||
            receipt.Images[0].Format != "ihex" || receipt.SourceStamp is null ||
            receipt.SourceStamp != await Debugging.DebugSourceStamp.ComputeAsync(root, token))
            throw new StudioXException("STC_ISP_BUILD", "当前源码、编译设置或固件目标已变化，请重新编译后下载。");
        var source = receipt.Images[0];
        var sourceImage = PathBoundary.Resolve(root, source.RelativePath);
        if (!File.Exists(sourceImage) || new FileInfo(sourceImage).Length > 1024 * 1024)
            throw new StudioXException("STC_ISP_IMAGE", "当前构建的 Intel HEX 固件不存在或异常过大。");
        var bytes = await File.ReadAllBytesAsync(sourceImage, token);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("STC_ISP_IMAGE", "固件已被修改，请重新编译。");
        // 与实际链接器 --code-size 取同一器件包上限；stcgal 的超界处理只是警告/截断。
        var limit = await StcCodeRomLimit.ReadAsync(root, project, token)
            ?? throw new StudioXException("STC_ISP_IMAGE", "缺少 STC 代码 Flash 容量配置。");
        var selectedLimit = (await ProjectBuildSettings.ReadAsync(root, token)).CodeRomSizeBytes ?? limit.MaximumBytes;
        var hex = StcIntelHex.Validate(bytes, checked((uint)Math.Min(limit.MaximumBytes, selectedLimit)));
        var tool = await GetToolStatusAsync(token);
        if (!tool.Available || tool.PythonExecutable is null) throw new StudioXException("STC_ISP_TOOL", tool.Message);
        return new(new(root, device.Id, device.FlashBytes, settings.Port.ToUpperInvariant(), sourceImage,
            hash, bytes.Length, hex.DataBytes, hex.HighestAddress, settings, tool), bytes);
    }

    /// <summary>便捷入口；调用方须在启动前就固件、目标和全片擦除取得用户确认。</summary>
    public async Task<StcIspReport> DownloadAsync(string root, StcIspSettings settings, IProgress<string>? output = null, CancellationToken token = default)
        => await DownloadPreparedAsync(await PrepareAsync(root, settings, token), output, token);

    /// <summary>只使用用户预览并确认过的固件快照；启动前再核对散列、型号及保存的时钟设置。</summary>
    public Task<StcIspReport> DownloadPreparedAsync(StcIspPreparation prepared, IProgress<string>? output = null, CancellationToken token = default)
        => Task.Run(() => DownloadPreparedCoreAsync(prepared, output, token), token);

    /// <summary>确认窗口可调用的无硬件复核；更改过的快照会在此被拒绝。</summary>
    public Task VerifyPreparedAsync(StcIspPreparation prepared, CancellationToken token = default)
        => Task.Run(() => VerifyPreparationAsync(prepared, token), token);

    private async Task<StcIspReport> DownloadPreparedCoreAsync(StcIspPreparation prepared, IProgress<string>? output, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token)) throw new StudioXException("STC_ISP_BUSY", "STC 串口下载正在进行。");
        try
        {
            await VerifyPreparationAsync(prepared, token);
            var settings = prepared.Settings;
            using var ownership = ProbeLease.Acquire();
            var log = new StringBuilder();
            var capture = new DownloadOutput(log, output);
            capture.Report($"STC ISP · {prepared.ExpectedModel} · {prepared.Port}\n固件：{prepared.SourceImage}\nSHA-256：{prepared.ImageSha256}\n准备日志：{prepared.LogPath}\n下载前请关闭占用该 COM 口的串口监视器。按开发板电源/下载键应在工具开始等待后进行。\n");
            var args = new List<string> { "-B", "-u", prepared.GuardScript, "--port", prepared.Port,
                "--expected-model", prepared.ExpectedModel, "--expected-sha256", prepared.ImageSha256,
                "--expected-code-bytes", prepared.ExpectedCodeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--image", prepared.Image, "--baud", settings.TransferBaud.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--clock-mode", settings.ClockMode switch { StcClockMode.InternalRc => "internal", StcClockMode.ExternalCrystal => "external", _ => "preserve" } };
            if (settings.ClockMode == StcClockMode.InternalRc && settings.ClockFrequencyHz is { } frequency)
                args.AddRange(["--frequency-hz", frequency.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            try
            {
                var result = await new ProcessRunner().RunAsync(new(prepared.PythonExecutable, args,
                    prepared.ProjectRoot, TimeSpan.FromMinutes(10), RemoveEnvironment: ["PYTHONHOME", "PYTHONPATH"], Output: capture), token);
                capture.Report($"\nexit={result.ExitCode}, timeout={result.TimedOut}, truncated={result.OutputTruncated}\n");
                var success = result.Success && capture.Text.Contains("STUDIOX_TARGET_VERIFIED " + prepared.ExpectedModel, StringComparison.Ordinal) &&
                    capture.Text.Contains("STUDIOX_PROGRAM_COMPLETE", StringComparison.Ordinal);
                return new(success, capture.Text, prepared.LogPath, result.ExitCode, result.TimedOut,
                    capture.Text.Contains("STUDIOX_TARGET_VERIFIED " + prepared.ExpectedModel, StringComparison.Ordinal));
            }
            catch (OperationCanceledException) { capture.Report("\n下载被取消；若已开始擦写，板内程序可能不完整。\n"); throw; }
            catch (Exception ex) { capture.Report("\n" + ex + "\n"); throw; }
            finally { await File.WriteAllTextAsync(prepared.LogPath, capture.Text, CancellationToken.None); }
        }
        finally { gate.Release(); }
    }

    private static async Task VerifyPreparationAsync(StcIspPreparation prepared, CancellationToken token)
    {
        var root = Path.GetFullPath(prepared.ProjectRoot);
        var (project, device) = await ReadProjectDeviceAsync(root, token);
        prepared.Settings.ValidateFor(StcIspCapabilities.For(device), requirePort: true);
        if (prepared.ExpectedModel != project.DeviceId || prepared.ExpectedCodeBytes != device.FlashBytes ||
            !string.Equals(prepared.Port, prepared.Settings.Port, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("STC_ISP_PREPARE", "下载准备的芯片型号、容量或串口与当前工程不符。");
        var saved = await StcIspSettings.ReadAsync(root, token);
        if (saved != prepared.Settings)
            throw new StudioXException("STC_ISP_PREPARE", "确认后串口、速度或时钟选项已变化，请重新准备并确认下载。");
        var session = Path.GetDirectoryName(prepared.LogPath);
        if (session is null || !string.Equals(Path.GetDirectoryName(session), PathBoundary.Resolve(root, ".build"), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(session).StartsWith("stc-isp-", StringComparison.Ordinal) ||
            !string.Equals(prepared.Image, Path.Combine(session, "firmware.hex"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(prepared.GuardScript, Path.Combine(session, "studiox-stcgal-guard.py"), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(prepared.Image) || !File.Exists(prepared.PythonExecutable))
            throw new StudioXException("STC_ISP_PREPARE", "下载快照、脚本或工具文件已消失或位置无效。");
        if (new FileInfo(prepared.Image).Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            new FileInfo(prepared.GuardScript).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new StudioXException("STC_ISP_PREPARE", "下载快照或型号门禁脚本不能是符号链接。");
        var bytes = await File.ReadAllBytesAsync(prepared.Image, token);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(prepared.ImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("STC_ISP_PREPARE", "确认后的固件快照被修改；未连接开发板。");
        var limit = await StcCodeRomLimit.ReadAsync(root, project, token)
            ?? throw new StudioXException("STC_ISP_IMAGE", "缺少 STC 代码 Flash 容量配置。");
        var selectedLimit = (await ProjectBuildSettings.ReadAsync(root, token)).CodeRomSizeBytes ?? limit.MaximumBytes;
        var hex = StcIntelHex.Validate(bytes, checked((uint)Math.Min(limit.MaximumBytes, selectedLimit)));
        if (hex.DataBytes != prepared.DataBytes || hex.HighestAddress != prepared.HighestAddress)
            throw new StudioXException("STC_ISP_PREPARE", "确认后的固件内容或地址范围已变化。");
        // 防止预览到执行期间工程目录中的脚本被替换；始终重新写入程序集内置的型号门禁。
        await using var resource = typeof(StcIspService).Assembly.GetManifestResourceStream("StudioX.Engine.Resources.studiox-stcgal-guard.py")
            ?? throw new StudioXException("STC_ISP_TOOL", "STC ISP 型号防护脚本缺失。");
        await using var destination = new FileStream(prepared.GuardScript, FileMode.Create, FileAccess.Write, FileShare.None);
        await resource.CopyToAsync(destination, token);
    }

    private static async Task<(ProjectManifest Project, DeviceDefinition Device)> ReadProjectDeviceAsync(string directory, CancellationToken token)
    {
        var root = Path.GetFullPath(directory);
        var project = await ProjectService.ReadAsync(root, token);
        if (project.Kind != ProjectKind.Pack || project.ToolsetId != "stc.sdcc" || project.CompilerId != "sdcc-4.5.0-15242")
            throw new StudioXException("STC_ISP_DEVICE", "当前工程不是 STC 8 位 SDCC 工程。");
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
        if (pack.Id != project.PackId || pack.Id != "stc.stc8" || pack.Version != project.PackVersion ||
            string.IsNullOrWhiteSpace(pack.Vendor) || !pack.Vendor.StartsWith("STC", StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("STC_ISP_DEVICE", "工程与 STC 器件包信息不一致。");
        var device = pack.Devices.SingleOrDefault(d => d.Id == project.DeviceId)
            ?? throw new StudioXException("STC_ISP_DEVICE", "器件包中找不到工程声明的精确型号。");
        if (device.Architecture != "mcs51" || device.ToolsetId != project.ToolsetId ||
            device.ToolsetVersion != project.ToolsetVersion || device.CompilerId != project.CompilerId || device.FlashOrigin != 0)
            throw new StudioXException("STC_ISP_DEVICE", "芯片架构、Flash 地址或工具链与工程不一致。");
        return (project, device);
    }

    private sealed class DownloadOutput(StringBuilder log, IProgress<string>? forward) : IProgress<string>
    {
        public void Report(string value) { lock (log) { if (log.Length < 5 * 1024 * 1024) log.Append(value); } forward?.Report(value); }
        public string Text { get { lock (log) return log.ToString(); } }
    }
}
