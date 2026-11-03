namespace StudioX.Engine.Pic;

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>调试构建、准确器件回读、独占租约与退出恢复；不使用外部 MPLAB 安装。</summary>
public sealed class PicDebugService(ToolsetCatalog catalog, PicBuildService builds, string dataDirectory)
{
    public const string ToolsetId = "microchip.mdb";
    public const string Version = "6.20.635";
    public const string CompilerId = "mdb-pickit3";
    public string? LogPath
    {
        get; private set;
    }

    public async Task<MdbDebugAdapter> StartAsync(string root, string? serial, IProgress<string>? output = null, CancellationToken token = default)
    {
        var storage = ProjectBuildStorage.Acquire(root, "pic");
        try
        {
            return await StartCoreAsync(root, serial, storage, output, token);
        }
        catch { storage.Dispose(); throw; }
    }

    private async Task<MdbDebugAdapter> StartCoreAsync(string root, string? serial, ProjectBuildStorage storage, IProgress<string>? output, CancellationToken token)
    {
        var project = await ProjectService.ReadAsync(root, token);
        var target = PicDebugTarget.Require(project.DeviceId);
        if (serial is not null && !Regex.IsMatch(serial, @"\A[A-Za-z0-9_-]{1,64}\z"))
        {
            throw new StudioXException("PIC_DEBUG_SERIAL", "PICkit3 序列号无效。");
        }
        var firmware = await builds.PreviewAsync(root, token);
        if (firmware.Debugger != "pickit3" || firmware.SourceRoot is null)
        {
            throw new StudioXException("PIC_DEBUG_BUILD", "请先成功构建 PICkit3 调试映像。");
        }
        var tools = await catalog.ResolveAsync(ToolsetId, Version, CompilerId, token);
        if (tools.Manifest.Purpose != "pic-mdb")
        {
            throw new StudioXException("PIC_DEBUG_TOOLS", "需要已校验的私有 MDB 运行组件。");
        }
        var lockPath = PathBoundary.Resolve(root, ".studiox/pic-debug-toolchain.lock.json");
        var contentLock = new ToolchainLock(1, ToolsetId, Version, tools.Fingerprint);
        if (File.Exists(lockPath) && await JsonStore.ReadAsync<ToolchainLock>(lockPath, token) != contentLock)
        {
            throw new StudioXException("PIC_DEBUG_TOOL_CHANGED", "本工程锁定的 MDB 组件内容或版本已变化，请通过组件管理核对后重新准备调试环境。");
        }
        if (!File.Exists(lockPath))
        {
            await JsonStore.WriteAsync(lockPath, contentLock, token);
        }
        var map = new PicDebugMemoryMap(PathBoundary.Resolve(tools.ResourceDirectory("pic"), "edc/" + project.DeviceId + ".PIC"), project.DeviceId);
        map.RequireDebugConfiguration(await File.ReadAllBytesAsync(firmware.HexPath, token), project.DeviceId);
        var directory = storage.Prepare("pic-debug");
        var elf = Path.Combine(directory, "firmware.elf");
        File.Copy(firmware.ElfPath, elf);
        var hex = Path.Combine(directory, "firmware.hex");
        File.Copy(firmware.HexPath, hex);
        if (Hash(hex) != firmware.Sha256)
        {
            throw new StudioXException("PIC_DEBUG_CHANGED", "调试 HEX 快照与构建凭据不符。");
        }
        var elfHash = Hash(elf);
        var backup = Path.Combine(directory, "before.hex");
        var backupHash = "";
        LogPath = Path.Combine(directory, "debug.log");
        var ipeLog = Path.Combine(directory, "programming.log");
        var toolLease = ToolUsageLease.Acquire(tools.RootDirectory);
        ProbeLease? probe = null;
        var attempted = false;
        var finished = false;
        async Task Finish()
        {
            if (finished)
            {
                return;
            }
            finished = true;
            try
            {
                if (attempted && serial is not null)
                {
                    if (Hash(backup) != backupHash)
                    {
                        throw new StudioXException("PIC_DEBUG_BACKUP_CHANGED", "原程序备份已改变，停止自动恢复。");
                    }
                    var restored = await IpeAsync(tools, ["-P" + project.Pic!.CompilerTarget, "-TPPK3", "-TS" + serial, "-F" + backup, "-Z", "-OID", "-M", "-Y", "-OL"], ipeLog, output, CancellationToken.None);
                    if (!restored.Contains("Verification successful.", StringComparison.Ordinal))
                    {
                        throw new StudioXException("PIC_DEBUG_RESTORE", "原程序恢复未取得明确校验成功记录。");
                    }
                    var after = Path.Combine(directory, "after.hex");
                    await IpeAsync(tools, ["-P" + project.Pic!.CompilerTarget, "-TPPK3", "-TS" + serial, "-I", "-GF" + after, "-OL"], ipeLog, output, CancellationToken.None);
                    var beforeBytes = map.Validate(await File.ReadAllBytesAsync(backup), true);
                    var afterBytes = map.Validate(await File.ReadAllBytesAsync(after), true);
                    if (beforeBytes.Count != afterBytes.Count || beforeBytes.Any(pair => !afterBytes.TryGetValue(pair.Key, out var value) || value != pair.Value))
                    {
                        throw new StudioXException("PIC_DEBUG_RESTORE", "退出回读与原备份不一致（包括程序、配置、EEPROM 和用户 ID）；备份已保留。");
                    }
                    await JsonStore.WriteAsync(Path.Combine(directory, "restored.json"), new
                    {
                        verified = true,
                        backupHash,
                        restoredUtc = DateTimeOffset.UtcNow
                    });
                }
            }
            finally { probe?.Dispose(); toolLease.Dispose(); storage.Dispose(); }
        }
        MdbDebugAdapter? adapter = null;
        try
        {
            probe = serial is null ? null : ProbeLease.Acquire();
            if (serial is not null)
            {
                var selection = new[] { "-P" + project.Pic!.CompilerTarget, "-TPPK3", "-TS" + serial };
                var response = await IpeAsync(tools, [.. selection, "-I", "-GF" + backup, "-OL"], ipeLog, output, token);
                var identities = Regex.Matches(response, @"(?im)^\s*Target device (\S+) found\.\s*$");
                if (identities.Count == 0 || identities.Any(m => m.Groups[1].Value != project.DeviceId))
                {
                    throw new StudioXException("PIC_TARGET_ID", "目标器件身份与当前工程不符，未写入调试程序。");
                }
                _ = map.Validate(await File.ReadAllBytesAsync(backup, token), true);
                backupHash = Hash(backup);
            }
            if (await builds.PreviewAsync(root, token) != firmware || Hash(elf) != elfHash)
            {
                throw new StudioXException("PIC_DEBUG_CHANGED", "回读期间工程或调试映像发生变化，请重新构建。");
            }
            if (serial is not null)
            {
                attempted = true;
                var download = await IpeAsync(tools, ["-P" + project.Pic!.CompilerTarget, "-TPPK3", "-TS" + serial, "-F" + hex, "-Z", "-OID", "-M", "-Y", "-OL"], ipeLog, output, token);
                if (!download.Contains("Verification successful.", StringComparison.Ordinal))
                {
                    throw new StudioXException("PIC_DEBUG_PROGRAM", "调试 HEX 未校验成功，不启动 MDB。");
                }
            }
            var process = new MdbProcess(tools, PathBoundary.Resolve(dataDirectory, "pic/mdb-user/" + Guid.NewGuid().ToString("N")), LogPath,
                serial is null ? null : project.DeviceId);
            adapter = new(process, root, firmware.SourceRoot, target.HardwareBreakpoints)
            {
                FinishSession = Finish,
                FileRegisterEnd = map.FileRegisterEnd,
                ProgramWordEnd = map.ProgramWordEnd
            };
            if (output is not null)
            {
                adapter.Trace += output.Report;
            }
            attempted = true;
            await adapter.InitializeAsync(project.Pic!.CompilerTarget, elf, serial, token);
            if (output is not null)
            {
                adapter.Trace -= output.Report;
            }
            return adapter;
        }
        catch (Exception failure)
        {
            try
            {
                if (adapter is not null)
                {
                    await adapter.DisposeAsync();
                }
                else
                {
                    await Finish();
                }
            }
            catch (Exception cleanup) { throw new AggregateException("PIC 调试启动失败，退出恢复也未完整确认；请保留原始日志和 before.hex。", failure, cleanup); }
            throw;
        }
    }

    private async Task<string> IpeAsync(ResolvedToolset tools, string[] arguments, string logPath, IProgress<string>? output, CancellationToken token)
    {
        var home = PathBoundary.Resolve(dataDirectory, "pic/mdb-ipe-user");
        Directory.CreateDirectory(home);
        var result = await new ProcessRunner().RunAsync(new(tools.Tool("java"), ["-Duser.home=" + home, "-Duser.language=en", "-Duser.country=US", "-Dfile.encoding=UTF-8", "-jar", tools.Tool("ipecmd"), .. arguments],
            home, TimeSpan.FromMinutes(5), ToolsetEnvironment.Create(tools), RemoveEnvironment: [.. ToolsetEnvironment.AmbientVariables, "JAVA_HOME", "JRE_HOME", "JAVA_TOOL_OPTIONS", "JDK_JAVA_OPTIONS", "_JAVA_OPTIONS", "CLASSPATH"],
            Output: output, StreamCompleteOutput: true, ContainProcessTree: true), token);
        var text = result.StandardOutput + "\n" + result.StandardError;
        await File.AppendAllTextAsync(logPath, string.Join(' ', arguments) + "\n" + text + "\n", CancellationToken.None);
        if (!result.Success || result.OutputTruncated)
        {
            throw new StudioXException("PIC_DEBUG_IPE", "PICkit3 回读或恢复失败，退出码 " + result.ExitCode + "。\n" + text);
        }
        return text;
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
