namespace StudioX.Engine;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>基础 MCU 外设映射使用厂商 VE 转换和 Supra，不执行用户逻辑、Quartus 或 Shell。</summary>
public sealed partial class Ag32PinMappingBuildService(ToolsetCatalog catalog, string? licenseDirectory = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public string LicenseDirectory
    {
        get;
    } = Path.GetFullPath(licenseDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX", "licenses", "ag32-pin-mapping"));
    public bool LicenseConfigured => File.Exists(PathBoundary.Resolve(LicenseDirectory, "license.txt"));

    public async Task ImportLicenseAsync(string sourceFile, CancellationToken token = default)
    {
        var source = Path.GetFullPath(sourceFile);
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 || new FileInfo(source).Length is <= 0 or > 64 * 1024)
        {
            throw new StudioXException("AG32_MAPPING_LICENSE", "请选择本机有效的厂商许可文件（1–65,536 字节）。");
        }
        var bytes = await File.ReadAllBytesAsync(source, token);
        if (bytes.Length is <= 0 or > 64 * 1024)
        {
            throw new StudioXException("AG32_MAPPING_LICENSE", "许可文件大小不合法。");
        }
        CheckPrivateDirectory();
        Directory.CreateDirectory(LicenseDirectory);
        CheckPrivateDirectory();
        var target = PathBoundary.Resolve(LicenseDirectory, "license.txt");
        var temporary = PathBoundary.Resolve(LicenseDirectory, "license-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public Task<Ag32PinMappingBuildReport> BuildAsync(string projectDirectory, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, IProgress<string>? output = null)
        => Task.Run(() => BuildCoreAsync(Path.GetFullPath(projectDirectory), progress, output, cancellationToken), cancellationToken);

    private async Task<Ag32PinMappingBuildReport> BuildCoreAsync(string root, IProgress<string>? progress, IProgress<string>? output, CancellationToken token)
    {
        var build = PathBoundary.Resolve(root, ".build/ag32-mapping");
        var logPath = PathBoundary.Resolve(root, ".build/ag32-mapping/studiox-mapping.log");
        var gate = Gates.GetOrAdd(root, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, token))
        {
            throw new StudioXException("AG32_MAPPING_BUSY", "本工程的引脚映射正在构建。");
        }
        var log = new StringBuilder();
        string? privateRun = null;
        try
        {
            Directory.CreateDirectory(build);
            Invalidate(root);
            var project = await ProjectService.ReadAsync(root, token);
            var settings = RequireSettings(project);
            var profile = await RequireDeviceIdentityAsync(root, project, token);
            progress?.Report("校验 AG32 引脚映射工具…");
            var tools = await catalog.ResolveAsync(settings.ToolsetId, settings.ToolsetVersion, settings.CompilerId, token, progress: progress);
            var pins = await Ag32PinPlanCatalog.ReadAsync(tools, settings.TargetDevice, profile.PinCount, root, token);
            var assignablePins = pins.Pins.Where(pin => pin.CanAssign).Select(pin => pin.Number).ToHashSet();
            var source = await Ag32PinMappingValidation.ReadSourceAsync(root, settings, assignablePins, profile, token);
            var sourceHash = Hash(source);
            var locked = new ToolchainLock(1, settings.ToolsetId, settings.ToolsetVersion, tools.Fingerprint);
            var lockPath = PathBoundary.Resolve(root, Ag32PinMappingReceipt.LockRelativePath);
            if (File.Exists(lockPath) && await JsonStore.ReadAsync<ToolchainLock>(lockPath, token) != locked)
            {
                throw new StudioXException("AG32_MAPPING_LOCK", "引脚映射工具与工程锁定内容不同，请检查安装版本。");
            }
            await JsonStore.WriteAsync(lockPath, locked, token);
            // 构建固定输入快照；用户在编译期间修改 VE 不会混入正在生成的镜像。
            var converterSource = source is [0xef, 0xbb, 0xbf, ..] ? source[3..] : source;
            await File.WriteAllBytesAsync(PathBoundary.Resolve(build, "pins.ve"), converterSource, token);
            var environment = ToolsetEnvironment.Create(tools);
            var removeEnvironment = ToolsetEnvironment.AmbientVariables.Append("ALTA_HOME").ToArray();
            progress?.Report("转换 VE 引脚与时钟配置…");
            var converter = await new ProcessRunner().RunAsync(new(tools.Tool("python"), ["-I", "-B", "-X", "utf8", tools.Tool("converter"),
                "-d", settings.TargetDevice, "-c", "pins.hx", "pins.ve", "pins.vx", "-x", "pins.vex"], build,
                TimeSpan.FromMinutes(2), environment, RemoveEnvironment: removeEnvironment, Output: output), token);
            AppendResult(log, "厂商 VE 转换", converter);
            if (!converter.Success)
            {
                return await FinishAsync(converter, []);
            }
            if (converter.StandardError.Contains("is ignored because no IP macro is specified", StringComparison.Ordinal))
            {
                // 厂商把未知功能名视为自定义模块端口，并仅警告后忽略；基础映射不能把这种空连接当成功。
                log.AppendLine("Error: 基础映射包含被厂商忽略的自定义或无效功能。请修正内部 GPIO / 外设功能名，或使用独立 Verilog 流程。");
                return await FinishAsync(converter with
                {
                    ExitCode = 1
                }, []);
            }
            await Ag32PinMappingClockVerification.CreateSdcAsync(build, token, basicMapping: true);
            await Ag32GpioElectrical.CreateAsync(build, source, token);
            privateRun = await PreparePrivateRuntimeAsync(tools, token);
            // Supra 将 ALTA_HOME 写入 Tcl source 表达式，Windows 反斜杠会变成转义字符。
            environment["ALTA_HOME"] = privateRun.Replace((char)92, '/');
            progress?.Report("编译 AG32 独立映射镜像（Supra）…");
            var supra = await new ProcessRunner().RunAsync(new(tools.Tool("supra"), ["-L", "logic_log.txt",
                "-X", "set LOGIC_TYPE VX", "-X", "set LOGIC_DB logic_db", "-X", "set LOGIC_DEVICE " + settings.TargetDevice,
                "-X", "set LOGIC_DESIGN pins", "-X", "set LOGIC_TOPPIN false", "-X", "set LOGIC_DIR .",
                "-X", "set LOGIC_VX pins.vx", "-X", "set VEX_FILE pins.vex", "-X", "set LOGIC_BIN pins.bin",
                "-X", "set BOARD_ASF studiox-gpio.asf", "-X", "set BOARD_PRE {}", "-X", "set BOARD_POST {}",
                "-X", "set IP_ASF {}", "-X", "set IP_PRE {}", "-X", "set IP_POST {}",
                "-X", "set DESIGN_ASF {}", "-X", "set DESIGN_PRE {}", "-X", "set DESIGN_POST {}",
                "-X", "set IP_SDC studiox-clocks.sdc", "-X", "set LOGIC_COMPRESS false",
                "-F", PathBoundary.Resolve(tools.ResourceDirectory("platform"), "gen_logic.tcl")],
                build, TimeSpan.FromMinutes(5), environment, RemoveEnvironment: removeEnvironment, Output: output), token);
            AppendResult(log, "厂商 Supra 编译", supra);
            if (!supra.Success)
            {
                return await FinishAsync(supra, []);
            }
            if ((supra.StandardOutput + supra.StandardError).Contains("is not assigned, placed at pin", StringComparison.Ordinal))
            {
                log.AppendLine("Error: Supra 存在未约束而自动分配的 IO；基础映射必须全部符合 VE 指定的物理引脚。");
                return await FinishAsync(supra with
                {
                    ExitCode = 1
                }, []);
            }
            var routing = await Ag32PinMappingRouting.VerifyAsync(build, source, token);
            await routing.Timing.ExportAsync(build, token);
            output?.Report(routing.Timing.Summary + "\n");
            foreach (var mapping in routing.Mappings)
            {
                log.AppendLine("[实际物理布线] " + mapping);
            }
            var imagePath = PathBoundary.Resolve(root, Ag32PinMappingReceipt.ImageRelativePath);
            if (!File.Exists(imagePath) || new FileInfo(imagePath).Length <= 0 || new FileInfo(imagePath).Length > profile.LogicImageBytes)
            {
                throw new StudioXException("AG32_MAPPING_IMAGE", "Supra 未生成有效的独立映射 BIN，或映像超过保留的 100 KiB 逻辑区。");
            }
            if (sourceHash != Hash(await Ag32PinMappingValidation.ReadSourceAsync(root, settings, assignablePins, profile, token)) || project != await ProjectService.ReadAsync(root, token))
            {
                throw new StudioXException("AG32_MAPPING_CHANGED", "构建期间 VE 或工程配置发生变化，请重新编译。");
            }
            // 工具在执行期间也不能被替换；验证缓存只在整树快照未变时复用。
            var after = await catalog.ResolveAsync(settings.ToolsetId, settings.ToolsetVersion, settings.CompilerId, token);
            if (after.Fingerprint != tools.Fingerprint)
            {
                throw new StudioXException("AG32_MAPPING_CHANGED", "构建期间映射工具发生变化。");
            }
            await using var image = File.OpenRead(imagePath);
            var imageHash = Convert.ToHexString(await SHA256.HashDataAsync(image, token));
            var receipt = new Ag32PinMappingReceipt(2, settings, sourceHash, tools.Fingerprint, imageHash, image.Length,
                routing.VexSha256, routing.IoAsfSha256, routing.RoutedSha256, routing.VxSha256, routing.HeaderSha256, routing.SdcSha256, routing.Timing.Sha256);
            await JsonStore.WriteAsync(PathBoundary.Resolve(root, Ag32PinMappingReceipt.RelativePath), receipt, token);
            log.AppendLine($"映射构建成功：{image.Length:N0} 字节；镜像 SHA-256 {imageHash}");
            return await FinishAsync(supra, [imagePath, PathBoundary.Resolve(build, "pins.hx"), PathBoundary.Resolve(build, "pins.vex"),
                PathBoundary.Resolve(build, Ag32GpioElectrical.FileName),
                PathBoundary.Resolve(build, "studiox-clocks.sdc"), PathBoundary.Resolve(build, "studiox-timing.json"),
                PathBoundary.Resolve(build, "coverage.rpt"), PathBoundary.Resolve(build, "setup_summary.rpt"), PathBoundary.Resolve(build, "hold_summary.rpt")]);
        }
        catch (Exception ex)
        {
            Invalidate(root);
            log.AppendLine(ex.ToString());
            await File.WriteAllTextAsync(logPath, log.ToString(), CancellationToken.None);
            throw;
        }
        finally
        {
            try
            {
                if (privateRun is not null)
                {
                    RemovePrivateRuntime(privateRun);
                }
            }
            finally { gate.Release(); }
        }
        async Task<Ag32PinMappingBuildReport> FinishAsync(ProcessResult result, string[] artifacts)
        {
            if (!result.Success)
            {
                Invalidate(root);
            }
            await File.WriteAllTextAsync(logPath, log.ToString(), token);
            return new(result.Success, artifacts, logPath, log.ToString(), result.ExitCode, result.TimedOut);
        }
    }

    public async Task<Ag32PinMappingStatus> InspectAsync(string projectDirectory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(projectDirectory);
        var project = await ProjectService.ReadAsync(root, token);
        if (project.PinMapping is not { } settings)
        {
            return new(false, null, null, false, ["工程未启用基础 VE 映射。"], LicenseConfigured);
        }
        try
        {
            await ValidateBuiltAsync(root, token);
            return new(true, PathBoundary.Resolve(root, settings.PinMapFile), PathBoundary.Resolve(root, Ag32PinMappingReceipt.ImageRelativePath), true,
                LicenseConfigured ? [] : ["未导入本机 Supra 许可；下次构建需要有效的厂商许可。"], LicenseConfigured);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(true, PathBoundary.Resolve(root, settings.PinMapFile), PathBoundary.Resolve(root, Ag32PinMappingReceipt.ImageRelativePath), false,
                [ex.Message], LicenseConfigured);
        }
    }

    public async Task<Ag32PinMappingImage> ValidateBuiltAsync(string projectDirectory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(projectDirectory);
        var project = await ProjectService.ReadAsync(root, token);
        var settings = RequireSettings(project);
        var profile = await RequireDeviceIdentityAsync(root, project, token);
        var path = PathBoundary.Resolve(root, Ag32PinMappingReceipt.RelativePath);
        if (!File.Exists(path))
        {
            throw new StudioXException("AG32_MAPPING_BUILD_REQUIRED", "请先编译当前 VE 引脚映射；没有有效映射构建凭据。");
        }
        var receipt = await JsonStore.ReadAsync<Ag32PinMappingReceipt>(path, token);
        var tools = await catalog.ResolveAsync(settings.ToolsetId, settings.ToolsetVersion, settings.CompilerId, token);
        var pins = await Ag32PinPlanCatalog.ReadAsync(tools, settings.TargetDevice, profile.PinCount, root, token);
        var assignablePins = pins.Pins.Where(pin => pin.CanAssign).Select(pin => pin.Number).ToHashSet();
        var sourceHash = Hash(await Ag32PinMappingValidation.ReadSourceAsync(root, settings, assignablePins, profile, token));
        var expectedLock = new ToolchainLock(1, settings.ToolsetId, settings.ToolsetVersion, tools.Fingerprint);
        var lockPath = PathBoundary.Resolve(root, Ag32PinMappingReceipt.LockRelativePath);
        if (receipt.FormatVersion != 2 || receipt.Settings != settings || receipt.SourceSha256 != sourceHash ||
            receipt.ToolFingerprint != tools.Fingerprint || !File.Exists(lockPath) || await JsonStore.ReadAsync<ToolchainLock>(lockPath, token) != expectedLock)
        {
            throw new StudioXException("AG32_MAPPING_STALE", "VE、器件或工具已变化，原映射镜像失效，请重新编译。");
        }
        var routing = await Ag32PinMappingRouting.VerifyAsync(PathBoundary.Resolve(root, ".build/ag32-mapping"),
            await Ag32PinMappingValidation.ReadSourceAsync(root, settings, assignablePins, profile, token), token);
        if (routing.VexSha256 != receipt.VexSha256 || routing.IoAsfSha256 != receipt.IoAsfSha256 || routing.RoutedSha256 != receipt.RoutedSha256 ||
            routing.VxSha256 != receipt.VxSha256 || routing.HeaderSha256 != receipt.HeaderSha256 || routing.SdcSha256 != receipt.SdcSha256 ||
            routing.Timing.Sha256 != receipt.TimingSha256)
        {
            throw new StudioXException("AG32_MAPPING_ROUTING", "物理引脚分配证据与构建凭据不符，请重新编译。");
        }
        var imagePath = PathBoundary.Resolve(root, Ag32PinMappingReceipt.ImageRelativePath);
        if (!File.Exists(imagePath))
        {
            throw new StudioXException("AG32_MAPPING_IMAGE", "映射镜像缺失，请重新编译。");
        }
        await using var stream = File.OpenRead(imagePath);
        if (stream.Length <= 0 || stream.Length > profile.LogicImageBytes || stream.Length != receipt.ImageBytes ||
            Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) != receipt.ImageSha256)
        {
            throw new StudioXException("AG32_MAPPING_IMAGE", "映射镜像与构建凭据不符，请重新编译。");
        }
        if (sourceHash != Hash(await Ag32PinMappingValidation.ReadSourceAsync(root, settings, assignablePins, profile, token)) ||
            await ProjectService.ReadAsync(root, token) != project)
        {
            throw new StudioXException("AG32_MAPPING_STALE", "核对映射期间 VE 或器件配置发生变化，请重新编译。");
        }
        return new(imagePath, receipt.ImageSha256, receipt.ImageBytes, sourceHash, tools.Fingerprint);
    }

    public Task<Ag32PinMappingImage> RequireImageAsync(string projectDirectory, CancellationToken token = default)
        => ValidateBuiltAsync(projectDirectory, token);

    public static void Invalidate(string projectDirectory)
    {
        // 原始厂商报告保留用于排错；失效后移除上一次成功构建导出的摘要，避免展示旧的正余量。
        foreach (var relative in new[] { Ag32PinMappingReceipt.RelativePath, Ag32PinMappingReceipt.ImageRelativePath,
            ".build/ag32-mapping/studiox-timing.json", ".build/ag32-mapping/coverage.rpt",
            ".build/ag32-mapping/setup_summary.rpt", ".build/ag32-mapping/hold_summary.rpt" })
        {
            var path = PathBoundary.Resolve(projectDirectory, relative);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static Ag32PinMappingProjectSettings RequireSettings(ProjectManifest project)
    {
        if (project.Logic is not null)
        {
            throw new StudioXException("AG32_MAPPING_CUSTOM_LOGIC", "含自定义 Verilog 的工程不能由基础映射后端替代；请使用独立逻辑综合流程。");
        }
        return project.PinMapping ?? throw new StudioXException("AG32_MAPPING_DISABLED", "本工程未启用基础 VE 引脚映射。");
    }

    private static async Task<Ag32DeviceProfile> RequireDeviceIdentityAsync(string root, ProjectManifest project, CancellationToken token)
    {
        var profile = Ag32DeviceCatalog.Require(project.DeviceId);
        var settings = RequireSettings(project);
        var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
        var device = pack.Devices.SingleOrDefault(item => item.Id == project.DeviceId);
        if (!profile.CanMap || settings != new Ag32PinMappingProjectSettings(profile.TargetDevice) ||
            pack.FormatVersion != 1 || pack.Id != project.PackId || pack.Version != project.PackVersion || pack.Vendor != "AGM" ||
            device is null || !profile.Matches(device) || device.ToolsetId != project.ToolsetId ||
            device.ToolsetVersion != project.ToolsetVersion || device.CompilerId != project.CompilerId)
        {
            throw new StudioXException("AG32_MAPPING_DEVICE", "工程的型号、封装、容量或锁定工具与已核实的 AGM 器件包不一致。");
        }
        return profile;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void AppendResult(StringBuilder log, string phase, ProcessResult result)
    {
        log.AppendLine($"[{phase}] exit={result.ExitCode}, timeout={result.TimedOut}, truncated={result.OutputTruncated}")
            .AppendLine(result.StandardOutput).AppendLine(result.StandardError);
    }
}
