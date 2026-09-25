namespace StudioX.Engine;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using StudioX.Engine.Debugging;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record DownloadOptions(string ProbeId, int SpeedKhz, string? Serial = null);
public sealed record DownloadConfiguration(DeviceDefinition Device, OpenOcdDefinition OpenOcd, DownloadOptions Options, string? TargetScriptText = null);
public sealed record DownloadReport(bool Success, string Log, string LogPath, int ExitCode, bool TimedOut)
{
    public string Summary => $"下载{(Success ? "成功" : "失败")}，退出代码：{ExitCode}" +
        (Success ? " · 已校验并复位运行" : TimedOut ? "（工具执行超时）" : " · 请查看 OpenOCD 日志");
}
public sealed record DownloadPreparation(DownloadConfiguration Configuration, DownloadOptions Options,
    ResolvedToolset Tools, string SourceImage, string Image, string[] Arguments, string LogPath);
public sealed record DownloadPreview(DownloadConfiguration Configuration, DownloadOptions Options,
    string SourceImage, string Format, string Sha256, long ImageBytes);

/// <summary>OpenOCD 单次下载会话；用户明确启动后才接触硬件。</summary>
public sealed class OpenOcdService(ToolsetCatalog catalog)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<DownloadConfiguration?> ConfigurationAsync(string projectDirectory, CancellationToken token = default)
    {
        var project = await ProjectService.ReadAsync(projectDirectory, token);
        DownloadConfiguration configuration;
        if (project.Kind == ProjectKind.CubeMx)
        {
            var imported = Stm32DownloadCatalog.Find(project);
            if (imported is null) return null;
            configuration = imported;
        }
        else
        {
            var pack = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(projectDirectory, "device/manifest.json"), token);
            var packDevice = pack.Devices.Single(d => d.Id == project.DeviceId);
            if (packDevice.OpenOcd is not { Probes.Count: > 0 } packDefinition) return null;
            // 保留包声明的兼容范围和协议，统一 DAP 在界面上的名称。
            packDefinition = packDefinition with { Probes = packDefinition.Probes.Select(p => p.Id == "cmsis-dap" ? p with { DisplayName = "DAP-Link (CMSIS-DAP)" } : p).ToArray() };
            configuration = new(packDevice, packDefinition, new(packDefinition.Probes[0].Id, packDefinition.Probes[0].DefaultSpeedKhz));
        }
        var device = configuration.Device;
        var definition = configuration.OpenOcd;
        if (device.ToolsetId != project.ToolsetId || device.ToolsetVersion != project.ToolsetVersion || device.CompilerId != project.CompilerId)
            throw new StudioXException("DOWNLOAD_TOOLSET", "工程与器件的下载工具集不一致。");
        var settings = PathBoundary.Resolve(projectDirectory, ".studiox/download.json");
        var options = File.Exists(settings) ? await JsonStore.ReadAsync<DownloadOptions>(settings, token) : configuration.Options;
        Validate(definition, options);
        return configuration with { Options = options };
    }

    public async Task SaveOptionsAsync(string projectDirectory, DownloadOptions options, CancellationToken token = default)
    {
        var configuration = await ConfigurationAsync(projectDirectory, token) ?? throw Unsupported();
        Validate(configuration.OpenOcd, options);
        await JsonStore.WriteAsync(PathBoundary.Resolve(projectDirectory, ".studiox/download.json"), options, token);
    }

    /// <summary>只准备并验证当前构建快照，不启动 OpenOCD、不访问烧录器。</summary>
    public Task<DownloadPreparation> PrepareAsync(string projectDirectory, DownloadOptions options, CancellationToken token = default)
        => Task.Run(() => PrepareCoreAsync(projectDirectory, options, token), token);

    /// <summary>只读检查当前构建产物；不创建下载快照，也不访问烧录器。</summary>
    public async Task<DownloadPreview> PreviewAsync(string projectDirectory, DownloadOptions options, CancellationToken token = default)
    {
        var validated = await ValidateImageAsync(projectDirectory, options, token);
        return new(validated.Configuration, options, validated.SourceImage, validated.Format,
            validated.Sha256, validated.Bytes.LongLength);
    }

    private sealed record ValidatedImage(DownloadConfiguration Configuration, ResolvedToolset Tools,
        string SourceImage, string Format, string Sha256, byte[] Bytes);

    private async Task<ValidatedImage> ValidateImageAsync(string projectDirectory, DownloadOptions options, CancellationToken token)
    {
        var root = Path.GetFullPath(projectDirectory);
        var project = await ProjectService.ReadAsync(root, token);
        var configuration = await ConfigurationAsync(root, token) ?? throw Unsupported();
        Validate(configuration.OpenOcd, options);
        var device = configuration.Device;
        var tools = await catalog.ResolveAsync(device.ToolsetId, device.ToolsetVersion, device.CompilerId, token);
        var expected = new ToolchainLock(1, device.ToolsetId, device.ToolsetVersion, tools.Fingerprint);
        var lockPath = PathBoundary.Resolve(root, ".studiox/toolchain.lock.json");
        var receiptPath = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
        if (!File.Exists(lockPath) || await JsonStore.ReadAsync<ToolchainLock>(lockPath, token) != expected || !File.Exists(receiptPath))
            throw new StudioXException("DOWNLOAD_BUILD", "请先使用当前工具集成功编译工程。");
        var receipt = await JsonStore.ReadAsync<BuildReceipt>(receiptPath, token);
        if (receipt.Project != project || receipt.ToolFingerprint != tools.Fingerprint || receipt.Images.Length == 0 ||
            receipt.SourceStamp is null ||
            receipt.SourceStamp != await DebugSourceStamp.ComputeAsync(root, token))
            throw new StudioXException("DOWNLOAD_BUILD", "源码或工程配置已变化，请重新编译后下载。");
        if (receipt.Images.Length != 1)
            throw new StudioXException("DOWNLOAD_TARGET", "工程包含多个可执行固件目标，无法自动确定下载对象；请保留一个应用固件目标后重试。");
        if (project.CubeMx is { } cube)
        {
            var ioc = await File.ReadAllLinesAsync(PathBoundary.Resolve(root, cube.IocFile), token);
            var currentDevice = ioc.FirstOrDefault(line => line.StartsWith("Mcu.CPN=", StringComparison.Ordinal))?[8..]
                ?? ioc.FirstOrDefault(line => line.StartsWith("Mcu.Name=", StringComparison.Ordinal))?[9..];
            if (!string.Equals(currentDevice?.Trim(), project.DeviceId, StringComparison.OrdinalIgnoreCase))
                throw new StudioXException("DOWNLOAD_DEVICE", "CubeMX 的芯片型号已改变，请重新导入工程后下载。");
        }
        var source = receipt.Images[0];
        var sourceImage = PathBoundary.Resolve(root, source.RelativePath);
        if (!File.Exists(sourceImage) || new FileInfo(sourceImage).Length > 64 * 1024 * 1024)
            throw new StudioXException("DOWNLOAD_IMAGE", "固件不存在或超过 64 MiB，请重新编译。");
        var bytes = await File.ReadAllBytesAsync(sourceImage, token);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("DOWNLOAD_CHANGED", "编译后的固件已被替换或修改，请重新编译后下载。");
        FirmwareImage.Validate(bytes, source.Format, device);
        return new(configuration, tools, sourceImage, source.Format, source.Sha256, bytes);
    }

    private async Task<DownloadPreparation> PrepareCoreAsync(string projectDirectory, DownloadOptions options, CancellationToken token,
        string? expectedDeviceId = null, string? expectedImageSha256 = null)
    {
        var root = Path.GetFullPath(projectDirectory);
        var validated = await ValidateImageAsync(root, options, token);
        if (expectedDeviceId is not null && !string.Equals(validated.Configuration.Device.Id, expectedDeviceId, StringComparison.Ordinal) ||
            expectedImageSha256 is not null && !string.Equals(validated.Sha256, expectedImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("DOWNLOAD_APPROVAL_CHANGED", "审批后的芯片型号或固件哈希已变化，请重新预览并授权。");
        // 使用构建产物快照，避免用户之后修改产物影响实际写入内容。
        var session = PathBoundary.Resolve(root, ".build/download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var image = Path.Combine(session, "firmware." + validated.Format);
        await File.WriteAllBytesAsync(image, validated.Bytes, token);
        var arguments = CreateArguments(root, validated.Configuration, options, validated.Tools, image, validated.Format);
        return new(validated.Configuration, options, validated.Tools, validated.SourceImage, image, arguments, Path.Combine(session, "openocd.log"));
    }

    public Task<DownloadReport> DownloadAsync(string projectDirectory, DownloadOptions options, IProgress<string>? output = null, CancellationToken token = default)
        => Task.Run(() => DownloadCoreAsync(projectDirectory, options, output, token, null, null), token);

    /// <summary>供逐次授权的调用方使用；执行前再次核对审批中的芯片与固件散列。</summary>
    public Task<DownloadReport> DownloadApprovedAsync(string projectDirectory, DownloadOptions options,
        string expectedDeviceId, string expectedImageSha256, IProgress<string>? output = null, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(expectedDeviceId) || expectedImageSha256 is null ||
            expectedImageSha256.Length != 64 || !expectedImageSha256.All(Uri.IsHexDigit))
            throw new StudioXException("DOWNLOAD_APPROVAL", "需要明确的芯片型号和完整固件 SHA-256。");
        return Task.Run(() => DownloadCoreAsync(projectDirectory, options, output, token,
            expectedDeviceId, expectedImageSha256), token);
    }

    private async Task<DownloadReport> DownloadCoreAsync(string projectDirectory, DownloadOptions options, IProgress<string>? output,
        CancellationToken token, string? expectedDeviceId, string? expectedImageSha256)
    {
        if (!await gate.WaitAsync(0, token)) throw new StudioXException("DOWNLOAD_BUSY", "烧录器正在使用中。");
        try
        {
            var root = Path.GetFullPath(projectDirectory);
            output?.Report("准备下载：检查当前构建产物与下载范围…\n");
            var prepared = await PrepareCoreAsync(root, options, token, expectedDeviceId, expectedImageSha256);
            using var ownership = ProbeLease.Acquire();
            var probe = Validate(prepared.Configuration.OpenOcd, options);
            var log = new StringBuilder();
            var capture = new DownloadOutput(log, output);
            capture.Report($"{prepared.Configuration.Device.Id} · {probe.DisplayName} · {options.SpeedKhz} kHz\n固件：{prepared.SourceImage}\n下载日志：{prepared.LogPath}\n");
            try
            {
                var result = await new ProcessRunner().RunAsync(new(prepared.Tools.Tool("openocd"), prepared.Arguments, root, TimeSpan.FromMinutes(5),
                    ToolsetEnvironment.Create(prepared.Tools), RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: capture), token);
                capture.Report($"\nexit={result.ExitCode}, timeout={result.TimedOut}, truncated={result.OutputTruncated}\n");
                var success = result.Success && (result.StandardOutput + result.StandardError).Contains("STUDIOX_DOWNLOAD_VERIFIED", StringComparison.Ordinal);
                return new(success, capture.Text, prepared.LogPath, result.ExitCode, result.TimedOut);
            }
            catch (OperationCanceledException) { capture.Report("\n下载已停止，未确认写入完成；请重新下载。\n"); throw; }
            catch (Exception ex) { capture.Report("\n" + ex + "\n"); throw; }
            finally { await File.WriteAllTextAsync(prepared.LogPath, capture.Text, CancellationToken.None); }
        }
        finally { gate.Release(); }
    }

    public static string[] CreateArguments(string projectDirectory, DownloadConfiguration configuration, DownloadOptions options, ResolvedToolset tools, string image, string format = "bin")
    {
        var probe = Validate(configuration.OpenOcd, options);
        // CH592 应用区为 448 KiB；CH595 的物理 Flash 为 256 KiB，但程序区仅前 240 KiB。
        // 在启动 OpenOCD 前核对包与下载配置，避免越过程序区或错用其他芯片脚本。
        var wirelessApplicationBytes = configuration.Device.Id.StartsWith("CH592", StringComparison.Ordinal) ? 448 * 1024 :
            configuration.Device.Id.StartsWith("CH595", StringComparison.Ordinal) ? 240 * 1024 : (int?)null;
        if (wirelessApplicationBytes is { } applicationBytes &&
            (WchDebugTarget.Find(configuration.Device) is null || configuration.TargetScriptText is not null ||
             configuration.OpenOcd.TargetScript != configuration.Device.OpenOcd!.TargetScript ||
             configuration.OpenOcd.ApplicationFlashBytes != applicationBytes || configuration.OpenOcd.Probes.Count != 1 ||
             probe.Id != "wch-link" || probe.Transport != "sdi" || probe.InterfaceScript != "interface/wch-link.cfg"))
            throw new StudioXException("DOWNLOAD_TARGET", "CH592/CH595 下载需要匹配的器件包、应用 Flash 范围及 WCH-Link SDI 配置。");
        var scripts = tools.ResourceDirectory("openocdScripts");
        var interfaceFile = PathBoundary.Resolve(scripts, probe.InterfaceScript);
        // 厂商接口也可随包提供纯 Tcl 配置，工具集本身保持版本不可变。
        if (!File.Exists(interfaceFile)) interfaceFile = PathBoundary.Resolve(projectDirectory, "device/" + probe.InterfaceScript);
        var targetFile = configuration.TargetScriptText is null ? PathBoundary.Resolve(projectDirectory, "device/" + configuration.OpenOcd.TargetScript) : null;
        if (!File.Exists(interfaceFile) || targetFile is not null && !File.Exists(targetFile)) throw new StudioXException("DOWNLOAD_CONFIG", "缺少烧录器或目标下载配置。");
        if (format is not ("bin" or "elf")) throw new StudioXException("DOWNLOAD_IMAGE", "不支持的下载格式。");
        // ELF 使用自己的绝对装载地址；BIN 才需要器件的 Flash 基地址。
        var address = format == "elf" ? "0" : "0x" + configuration.Device.FlashOrigin.ToString("x8", CultureInfo.InvariantCulture);
        // 沁恒分支基于 0.11，保留下划线形式的服务端口命令。
        var legacy = probe.Transport == "sdi";
        List<string> arguments = ["-s", scripts, "-c", legacy ? "gdb_port disabled" : "gdb port disabled", "-c", legacy ? "tcl_port disabled" : "tcl port disabled", "-c", legacy ? "telnet_port disabled" : "telnet port disabled",
            "-f", interfaceFile, "-c", "transport select " + probe.Transport];
        if (!string.IsNullOrWhiteSpace(options.Serial)) { arguments.Add("-c"); arguments.Add("adapter serial " + TclString(options.Serial)); }
        arguments.AddRange(targetFile is not null ? ["-f", targetFile] : ["-c", configuration.TargetScriptText!]);
        arguments.AddRange(["-c", "adapter speed " + options.SpeedKhz.ToString(CultureInfo.InvariantCulture)]);
        // 先核对硅片系列和容量，再按映像范围擦写；不执行全片擦除、解锁或选项字节写入。
        // WCH 0.11 的 reset run 在实板上仍停在复位入口；明确复位后 resume 才会执行用户程序。
        var restart = legacy ? "reset halt; resume" : "reset run";
        var operation = $"reset halt; studiox_check_target; echo [flash write_image erase {TclString(image.Replace('\\', '/'))} {address} {format}]; echo [verify_image {TclString(image.Replace('\\', '/'))} {address} {format}]; {restart}; echo STUDIOX_DOWNLOAD_VERIFIED";
        arguments.AddRange(["-c", probe.Transport == "sdi"
            ? "init; set failed [catch {" + operation + "} detail]; if {$failed} {catch {resume}; echo $detail}; shutdown; if {$failed} {error $detail}"
            : "init; " + operation + "; shutdown"]);
        return arguments.ToArray();
    }

    private static DebugProbeDefinition Validate(OpenOcdDefinition definition, DownloadOptions options)
    {
        var probe = definition.Probes.SingleOrDefault(p => p.Id == options.ProbeId) ?? throw new StudioXException("DOWNLOAD_PROBE", "请选择当前器件支持的烧录器。");
        if (options.SpeedKhz is < 100 or > 15000 || options.Serial?.Length > 100 || options.Serial?.Any(char.IsControl) == true ||
            probe.Transport is not ("swd" or "jtag" or "hla_swd" or "dapdirect_swd" or "sdi"))
            throw new StudioXException("DOWNLOAD_OPTIONS", "速度范围为 100–15000 kHz，序列号不能包含控制字符。");
        if (probe.Transport == "sdi" && (probe.Id != "wch-link" || options.SpeedKhz is not (400 or 4000 or 6000) || !string.IsNullOrWhiteSpace(options.Serial)))
            throw new StudioXException("DOWNLOAD_OPTIONS", "WCH-Link 支持 400、4000、6000 kHz；当前仅支持单台连接，请留空序列号。");
        return probe;
    }

    private static string TclString(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("[", "\\[").Replace("]", "\\]") + "\"";

    private static StudioXException Unsupported() => new("DOWNLOAD_UNSUPPORTED", "当前器件尚无匹配的下载配置；器件包需提供 OpenOCD 配置，CubeMX 当前支持目录中的 STM32F1/F4 型号。");
    private sealed class DownloadOutput(StringBuilder log, IProgress<string>? forward) : IProgress<string>
    {
        public void Report(string value) { lock (log) { if (log.Length < 5 * 1024 * 1024) log.Append(value); } forward?.Report(value); }
        public string Text { get { lock (log) return log.ToString(); } }
    }
}
