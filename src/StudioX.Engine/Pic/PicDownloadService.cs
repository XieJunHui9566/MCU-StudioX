namespace StudioX.Engine.Pic;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>使用 StudioX 管理的独立 IPE 运行组件；每次写入前重新校验构建、工具、序列号和目标回读。</summary>
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
        var image = PicHexImage.Validate(await File.ReadAllBytesAsync(firmware.HexPath, token));
        var receipt = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, PicBuildReceipt.RelativePath), token);
        var approval = Convert.ToHexString(SHA256.HashData([.. receipt, .. JsonSerializer.SerializeToUtf8Bytes(settings, JsonStore.Options)]));
        return new(firmware, settings, image, tools.Manifest.DisplayName ?? "PICkit3 运行组件 6.20", approval);
    }

    public async Task<PicDownloadReport> DownloadApprovedAsync(string root, string approvalId,
        IProgress<string>? output = null, CancellationToken token = default)
    {
        using var probe = ProbeLease.Acquire();
        var preview = await PreviewAsync(root, token);
        RequireApproval(preview, approvalId);
        var tools = await ResolveAsync(token);
        using var toolLease = ToolUsageLease.Acquire(tools.RootDirectory);
        var directory = PathBoundary.Resolve(root, ".build/pic-download/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var logPath = Path.Combine(directory, "download.log");
        var backupPath = Path.Combine(directory, "before.hex");
        using var log = new PicDownloadLog(logPath, output);
        try
        {
            var firmware = Path.Combine(directory, "firmware.hex");
            var content = await File.ReadAllBytesAsync(preview.Firmware.HexPath, token);
            if (Convert.ToHexString(SHA256.HashData(content)) != preview.Firmware.Sha256)
            {
                throw new StudioXException("PIC_DOWNLOAD_CHANGED", "固件已变化，请重新确认。");
            }
            await File.WriteAllBytesAsync(firmware, content, token);
            var listed = await RunAsync(tools, ["-T"], log, token);
            RequireList(listed);
            if (!ParseProgrammers(listed.StandardOutput + "\n" + listed.StandardError).Any(item => item.Serial == preview.Settings.ProgrammerSerial))
            {
                throw new StudioXException("PIC_PROGRAMMER_MISSING", "所选 PICkit3 当前不存在，请刷新列表并明确选择。");
            }
            var selection = new[] { "-P16F877A", "-TPPK3", "-TS" + preview.Settings.ProgrammerSerial };
            log.Report("\n回读并备份 PIC16F877A；目标板独立供电。\n");
            var read = await RunAsync(tools, [.. selection, "-I", "-GF" + backupPath, "-OL"], log, token);
            RequireSuccess(read, "PIC_BACKUP");
            RequireDevice(read.StandardOutput + "\n" + read.StandardError);
            if (!File.Exists(backupPath))
            {
                throw new StudioXException("PIC_BACKUP", "工具未生成目标回读备份，未启动写入。");
            }
            _ = PicHexImage.Validate(await File.ReadAllBytesAsync(backupPath, token), backup: true);
            // 回读期间发生重编译、源码或配置变化时，旧确认不能继续使用。
            RequireApproval(await PreviewAsync(root, token), approvalId);
            if (!(await File.ReadAllBytesAsync(firmware, token)).AsSpan().SequenceEqual(content))
            {
                throw new StudioXException("PIC_DOWNLOAD_CHANGED", "下载快照已变化。");
            }
            log.Report("\n开始写入与校验程序和配置；保留 EEPROM 与用户 ID。\n");
            var arguments = new List<string>(selection) { "-F" + firmware, "-Z", "-OID", "-M", "-Y" };
            if (preview.Settings.ReleaseAfterProgramming)
            {
                arguments.Add("-OL");
            }
            var result = await RunAsync(tools, arguments, log, token);
            var text = result.StandardOutput + "\n" + result.StandardError;
            // -M 自带的 Programming/Verify complete 不能代替随后显式 -Y 的最终结果。
            var success = result.Success && !result.OutputTruncated && Regex.IsMatch(text,
                @"(?im)^\s*Verification successful\.\s*$");
            await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
            {
                success,
                preview,
                backupPath,
                result
            }, CancellationToken.None);
            return new(success, result, logPath, backupPath);
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
            throw new StudioXException("PIC_TARGET_ID", "未确认 PIC16F877A 的真实器件 ID，未启动写入；请检查型号、供电和 ICSP 接线。");
        }
    }

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
        if (project.DeviceId != "PIC16F877A")
        {
            throw new StudioXException("PIC_DOWNLOAD_DEVICE", "当前 PICkit3 下载适配仅开放 PIC16F877A；其他 PIC 型号仍可编译和导出。");
        }
    }
}
