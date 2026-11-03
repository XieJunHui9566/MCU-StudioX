namespace StudioX.Engine.Pic;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>直接写入选定固件；只预检器件与保护配置，不回读、备份或恢复原程序。</summary>
public sealed class PicDownloadService(ToolsetCatalog catalog, PicBuildService builds, string dataDirectory)
{
    private static readonly string[] JavaEnvironment = [.. ToolsetEnvironment.AmbientVariables, "JAVA_HOME", "JRE_HOME", "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH"];

    public async Task<PicDownloadSettings> LoadSettingsAsync(string root, CancellationToken token = default)
    {
        await RequireProjectAsync(root, token);
        var path = PathBoundary.Resolve(root, PicDownloadSettings.RelativePath);
        var settings = File.Exists(path) ? await JsonStore.ReadAsync<PicDownloadSettings>(path, token) : new();
        settings.Validate(false);
        return settings;
    }

    public async Task SaveSettingsAsync(string root, string serial, bool release, CancellationToken token = default)
    {
        await RequireProjectAsync(root, token);
        var tools = await ResolveAsync(token);
        var settings = new PicDownloadSettings(ToolFingerprint: tools.Fingerprint, ProgrammerSerial: serial, ReleaseAfterProgramming: release);
        settings.Validate();
        await JsonStore.WriteAsync(PathBoundary.Resolve(root, PicDownloadSettings.RelativePath), settings, token);
    }

    /// <summary>只枚举 USB 编程器，不连接或供电目标。</summary>
    public async Task<PicProgrammer[]> ListAsync(IProgress<string>? output = null, CancellationToken token = default)
    {
        using var probe = ProbeLease.Acquire();
        var tools = await ResolveAsync(token);
        var result = await RunAsync(tools, ["-T"], output, token);
        RequireList(result);
        return ParseProgrammers(result.StandardOutput + "\n" + result.StandardError);
    }

    public static PicProgrammer[] ParseProgrammers(string text) => Regex.Matches(text,
        @"(?im)^\s*(?:\d+\s*[)\.]?\s*)?PICkit\s*3\s+S\.No\s*:\s*([A-Za-z0-9_-]{1,64})\s*$")
        .Select(match => new PicProgrammer(match.Groups[1].Value)).Distinct().ToArray();

    public async Task<PicDownloadPreview> PreviewAsync(string root, CancellationToken token = default)
    {
        await RequireProjectAsync(root, token);
        var settings = await LoadSettingsAsync(root, token);
        settings.Validate();
        var tools = await ResolveAsync(token);
        if (settings.ToolFingerprint != tools.Fingerprint)
        {
            throw new StudioXException("PIC_TOOL_CHANGED", "PICkit3 运行组件已变化，请重新选择烧录器并保存设置。");
        }
        var firmware = await builds.PreviewAsync(root, token);
        if (firmware.DeviceId != "PIC16F877A")
        {
            throw new StudioXException("PIC_DOWNLOAD_DEVICE", "普通下载入口当前仅完成 PIC16F877A 的镜像预检；其他型号请使用其调试构建和 MDB 入口。");
        }
        var image = PicHexImage.Validate(await File.ReadAllBytesAsync(firmware.HexPath, token));
        var receipt = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, PicBuildReceipt.RelativePath), token);
        var approval = Convert.ToHexString(SHA256.HashData([.. receipt, .. JsonSerializer.SerializeToUtf8Bytes(settings, JsonStore.Options)]));
        return new(firmware, settings, image, tools.Manifest.DisplayName ?? "PICkit3 运行组件 6.20", approval);
    }

    public async Task<PicDownloadReport> DownloadApprovedAsync(string root, string approvalId,
        IProgress<string>? output = null, CancellationToken token = default)
    {
        using var storage = ProjectBuildStorage.Acquire(root, "pic");
        using var probe = ProbeLease.Acquire();
        var preview = await PreviewAsync(root, token);
        RequireApproval(preview, approvalId);
        var tools = await ResolveAsync(token);
        using var toolLease = ToolUsageLease.Acquire(tools.RootDirectory);
        var directory = storage.Prepare("pic-download");
        var logPath = Path.Combine(directory, "download.log");
        using var log = new PicDownloadLog(logPath, output);
        var elapsed = Stopwatch.StartNew();
        try
        {
            var firmware = Path.Combine(directory, "firmware.hex");
            var content = await File.ReadAllBytesAsync(preview.Firmware.HexPath, token);
            if (Convert.ToHexString(SHA256.HashData(content)) != preview.Firmware.Sha256)
            {
                throw new StudioXException("PIC_DOWNLOAD_CHANGED", "固件已变化，请重新确认。");
            }
            await File.WriteAllBytesAsync(firmware, content, token);
            var selection = new[] { "-P16F877A", "-TPPK3", "-TS" + preview.Settings.ProgrammerSerial };
            log.Report("\n[1/2] 核对 PICkit3、PIC16F877A 与配置保护；目标板独立供电，不读取原程序。\n");
            var stage = Stopwatch.StartNew();
            // -GC 只选择配置区；拒绝保护中的目标，避免保留 EEPROM 时隐式解除保护。
            var identified = await RunAsync(tools, [.. selection, "-I", "-GC", "-OL"], log, token);
            stage.Stop();
            var identifyMilliseconds = stage.Elapsed.TotalMilliseconds;
            RequireSuccess(identified, "PIC_IDENTIFY");
            var identity = identified.StandardOutput + "\n" + identified.StandardError;
            if (HasFailure(identity) || !Regex.IsMatch(identity, @"(?im)^[ \t]*Operation Succeeded[ \t]*\r?$"))
            {
                throw new StudioXException("PIC_IDENTIFY", "PICkit3 器件与配置预检未明确完成，未启动写入；请查看原始日志。");
            }
            RequireProgrammer(identity, preview.Settings.ProgrammerSerial);
            RequireDevice(identity);
            RequireUnprotectedConfiguration(identity);
            log.Report($"\n器件与保护配置核对完成，耗时 {stage.Elapsed.TotalSeconds:F2} 秒。\n");
            // 只读预检期间发生重编译、源码或配置变化时，旧凭据不能继续使用。
            RequireApproval(await PreviewAsync(root, token), approvalId);
            if (!(await File.ReadAllBytesAsync(firmware, token)).AsSpan().SequenceEqual(content))
            {
                throw new StudioXException("PIC_DOWNLOAD_CHANGED", "下载快照已变化。");
            }
            log.Report("\n[2/2] 写入选定程序与配置并校验；保留 EEPROM 与用户 ID，不备份或恢复原程序。\n");
            // IPECMD 6.20 的 -M 已包含编程后校验；额外 -Y 会重复校验整个器件。
            var arguments = new List<string>(selection) { "-F" + firmware, "-Z", "-OID", "-M" };
            if (preview.Settings.ReleaseAfterProgramming)
            {
                arguments.Add("-OL");
            }
            stage.Restart();
            var result = await RunAsync(tools, arguments, log, token);
            stage.Stop();
            var programMilliseconds = stage.Elapsed.TotalMilliseconds;
            var text = result.StandardOutput + "\n" + result.StandardError;
            var success = result.Success && !result.OutputTruncated &&
                Regex.IsMatch(text, @"(?im)^[ \t]*Programming/Verify complete[ \t]*\r?$") &&
                Regex.IsMatch(text, @"(?im)^[ \t]*Program Succeeded\.[ \t]*\r?$") &&
                Regex.IsMatch(text, @"(?im)^[ \t]*Operation Succeeded[ \t]*\r?$") &&
                !HasFailure(text);
            if (success)
            {
                RequireProgrammer(text, preview.Settings.ProgrammerSerial);
                RequireDevice(text);
            }
            // 下载中的编辑或设置变化不能让旧镜像取得当前工程的成功凭据；不自动恢复旧固件。
            RequireApproval(await PreviewAsync(root, token), approvalId);
            if (!(await File.ReadAllBytesAsync(firmware, token)).AsSpan().SequenceEqual(content))
            {
                throw new StudioXException("PIC_DOWNLOAD_CHANGED", "下载快照已变化。");
            }
            log.Report($"\n写入与校验阶段耗时 {stage.Elapsed.TotalSeconds:F2} 秒；本次下载共 2 次 IPECMD 调用，总耗时 {elapsed.Elapsed.TotalSeconds:F2} 秒。\n");
            await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
            {
                success,
                preview,
                backupPath = (string?)null,
                backendInvocations = 2,
                identifyMilliseconds,
                programMilliseconds,
                totalMilliseconds = elapsed.Elapsed.TotalMilliseconds,
                result
            }, CancellationToken.None);
            return new(success, result, logPath);
        }
        catch (Exception failure)
        {
            log.Record("\n" + failure + "\n");
            failure.Data["PicDownloadLog"] = logPath;
            throw;
        }
    }

    private static void RequireDevice(string text)
    {
        // IPECMD 6.20 先按其 DFP 比较真实 silicon ID，成功后输出准确型号；-I 只打印 revision ID。
        var targets = Regex.Matches(text, @"(?im)^\s*Target device (\S+) found\.\s*$");
        if (targets.Count == 0 || targets.Any(match => match.Groups[1].Value != "PIC16F877A"))
        {
            throw new StudioXException("PIC_TARGET_ID", "未确认 PIC16F877A 的真实器件 ID；请检查型号、供电、ICSP 接线与原始工具日志。");
        }
    }

    private static void RequireProgrammer(string text, string serial)
    {
        var selected = Regex.Matches(text, @"(?im)^[ \t]*Selected Programming Tool Sno:[ \t]*\r?\n[ \t]*([A-Za-z0-9_-]{1,64})[ \t]*\r?$");
        if (selected.Count == 0 || selected.Any(match => match.Groups[1].Value != serial))
        {
            throw new StudioXException("PIC_PROGRAMMER_ID", "工具未确认选定的 PICkit3 序列号，请检查烧录器连接与原始日志。");
        }
    }

    private static void RequireUnprotectedConfiguration(string text)
    {
        // IPECMD 6.20 MemoryView.showConfigMemory 输出 DFP 字地址及十六进制配置值。
        var words = Regex.Matches(text, @"(?im)^[ \t]*2007\b([^\r\n]*)\r?$");
        var value = words.Count == 1 ? words[0].Groups[1].Value.Trim() : "";
        if (words.Count != 1 || !Regex.IsMatch(value, @"\A[0-9A-Fa-f]{1,4}\z") || !ushort.TryParse(value,
                System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var configuration) || configuration > 0x3fff)
        {
            throw new StudioXException("PIC_TARGET_CONFIG", "未取得唯一有效的 PIC16F877A 配置字，未启动写入；请查看原始日志。");
        }
        // CP 位 13、CPD 位 8：不能为普通下载解除保护并冒险擦除受保护的 EEPROM。
        if ((configuration & 0x2100) != 0x2100)
        {
            throw new StudioXException("PIC_CODE_PROTECTED", "目标程序或 EEPROM 处于代码保护中；本入口不会自动解除保护，未启动写入。");
        }
    }

    private static bool HasFailure(string text) => Regex.IsMatch(text,
        @"(?im)^[^\r\n]*(?:\bfailed\b|\bfailure\b|\berror\b)[^\r\n]*$");

    private static void RequireApproval(PicDownloadPreview preview, string approval)
    {
        if (string.IsNullOrWhiteSpace(approval) || preview.ApprovalId != approval)
        {
            throw new StudioXException("PIC_DOWNLOAD_CHANGED", "构建、固件或烧录设置已变化，请重新预览确认。");
        }
    }

    private static void RequireSuccess(ProcessResult result, string code)
    {
        if (!result.Success || result.OutputTruncated)
        {
            throw new StudioXException(code, $"PICkit3 工具未成功完成（退出码 {result.ExitCode}）。\n{result.StandardOutput}\n{result.StandardError}");
        }
    }

    private static void RequireList(ProcessResult result)
    {
        // 官方 6.20 的纯 -T 在输出完整列表后仍返回 OP_MISC=50；仅此只读命令接受严格的列表输出。
        if (!result.TimedOut && !result.OutputTruncated && result.ExitCode == 50 && string.IsNullOrWhiteSpace(result.StandardError) &&
            Regex.IsMatch(result.StandardOutput, @"\A\s*Available Tool List\r?\n-+\r?\n(?:\s*\d+\s+[^\r\n]+\s+S\.No\s*:\s*[A-Za-z0-9_-]{1,64}\s*\r?\n)*\s*\z"))
        {
            return;
        }
        RequireSuccess(result, "PIC_PROGRAMMER_LIST");
    }

    private async Task<ResolvedToolset> ResolveAsync(CancellationToken token)
    {
        var tools = await catalog.ResolveAsync(PicDownloadSettings.ToolsetId, "6.20.0", PicDownloadSettings.CompilerId, token);
        if (tools.Manifest.Purpose != "pic-ipe" || tools.Manifest.ComponentVersions?.GetValueOrDefault("ipe") != "6.20")
        {
            throw new StudioXException("PIC_IPE_VERSION", "需要 StudioX 管理的 PICkit3 兼容运行组件 IPE 6.20。");
        }
        return tools;
    }

    private async Task<ProcessResult> RunAsync(ResolvedToolset tools, IReadOnlyList<string> arguments, IProgress<string>? output, CancellationToken token)
    {
        var home = PathBoundary.Resolve(dataDirectory, "pic/ipe-user");
        Directory.CreateDirectory(home);
        return await new ProcessRunner().RunAsync(new(tools.Tool("java"), ["-Duser.home=" + home, "-Duser.language=en", "-Duser.country=US", "-Dfile.encoding=UTF-8", "-jar", tools.Tool("ipecmd"), .. arguments],
            home, TimeSpan.FromMinutes(10), ToolsetEnvironment.Create(tools), RemoveEnvironment: JavaEnvironment,
            Output: output, StreamCompleteOutput: true, ContainProcessTree: true), token);
    }

    private static async Task RequireProjectAsync(string root, CancellationToken token)
    {
        var project = await ProjectService.ReadAsync(root, token);
        await PicProject.ValidateAsync(root, project, token);
    }
}
