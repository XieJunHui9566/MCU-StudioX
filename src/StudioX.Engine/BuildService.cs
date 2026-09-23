namespace StudioX.Engine;

using System.Text;
using StudioX.Foundation;

public sealed class BuildService(ToolsetCatalog catalog)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public Task<ProjectBuildSettings> LoadSettingsAsync(string directory, CancellationToken token = default) => ProjectBuildSettings.ReadAsync(directory, token);
    public async Task<StcCodeRomLimit?> ReadStcCodeRomLimitAsync(string directory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(directory);
        return await StcCodeRomLimit.ReadAsync(root, await ProjectService.ReadAsync(root, token), token);
    }
    public async Task SaveSettingsAsync(string directory, ProjectBuildSettings settings, CancellationToken token = default)
    {
        settings.Validate();
        if (!await gate.WaitAsync(0, token)) throw new StudioXException("BUILD_BUSY", "构建期间不能修改编译参数。");
        try
        {
            var root = Path.GetFullPath(directory);
            var project = await ProjectService.ReadAsync(root, token);
            settings.ValidateFor(project);
            (await StcCodeRomLimit.ReadAsync(root, project, token))?.Validate(settings.CodeRomSizeBytes);
            if (await ProjectBuildSettings.ReadAsync(root, token) == settings) return;
            await JsonStore.WriteAsync(PathBoundary.Resolve(root, ProjectBuildSettings.RelativePath), settings, token);
            foreach (var relative in new[] { BuildReceipt.RelativePath, BuildMemoryService.SnapshotPath })
            {
                var path = PathBoundary.Resolve(root, relative);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        finally { gate.Release(); }
    }
    public Task<BuildReport> BuildAsync(string projectDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default, IProgress<string>? output = null)
        => Task.Run(() => ExecuteAsync(projectDirectory, false, progress, cancellationToken, output), cancellationToken);
    public Task<BuildReport> ConfigureAsync(string projectDirectory, IProgress<string>? progress = null, CancellationToken cancellationToken = default, IProgress<string>? output = null)
        => Task.Run(() => ExecuteAsync(projectDirectory, true, progress, cancellationToken, output), cancellationToken);
    private async Task<BuildReport> ExecuteAsync(string projectDirectory, bool configureOnly, IProgress<string>? progress, CancellationToken cancellationToken, IProgress<string>? output)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new StudioXException("BUILD_BUSY", "构建正在进行。");
        try
        {
            var root = Path.GetFullPath(projectDirectory);
            if (!configureOnly)
            {
                var memorySnapshot = PathBoundary.Resolve(root, BuildMemoryService.SnapshotPath);
                if (File.Exists(memorySnapshot)) File.Delete(memorySnapshot);
            }
            // 失败、取消或仅配置都不能留下可下载的旧构建凭据。
            var receiptPath = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
            if (File.Exists(receiptPath)) File.Delete(receiptPath);
            var project = await ProjectService.ReadAsync(root, cancellationToken);
            var sourceStamp = await Debugging.DebugSourceStamp.ComputeAsync(root, cancellationToken);
            var settings = await ProjectBuildSettings.ReadAsync(root, cancellationToken);
            settings.ValidateFor(project);
            var sdcc = project.ToolsetId == "stc.sdcc";
            var stcRom = await StcCodeRomLimit.ReadAsync(root, project, cancellationToken);
            stcRom?.Validate(settings.CodeRomSizeBytes);
            var stcIsp = sdcc ? await StcIspSettings.ReadAsync(root, cancellationToken) : null;
            int? stcClockHz = stcIsp is { ClockMode: not StcClockMode.Preserve, ClockFrequencyHz: { } hz }
                ? hz : null;
            progress?.Report("检查内置工具集…");
            var tools = await catalog.ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId, cancellationToken, progress: progress);
            var lockPath = Path.Combine(root, ".studiox", "toolchain.lock.json");
            var expected = new ToolchainLock(1, project.ToolsetId, project.ToolsetVersion, tools.Fingerprint);
            if (File.Exists(lockPath) && await JsonStore.ReadAsync<ToolchainLock>(lockPath, cancellationToken) != expected)
                throw new StudioXException("TOOLCHAIN_LOCK", "当前工具集与工程锁定内容不同，请检查安装版本。");
            await JsonStore.WriteAsync(lockPath, expected, cancellationToken);
            var build = Path.Combine(root, ".build");
            Directory.CreateDirectory(build);
            var log = new StringBuilder();
            log.AppendLine("工程编译参数：" + settings.SummaryFor(project));
            if (sdcc) log.AppendLine("STC 编译时钟宏：" + (stcClockHz is { } compiledHz ? $"STUDIOX_CLOCK_HZ={compiledHz}UL" : "未指定"));
            var logPath = Path.Combine(build, "studiox-build.log");
            string[] cubeExecutables = [];
            async Task<BuildReport> CompleteAsync(ProcessResult result, string[] artifacts)
            {
                var report = new BuildReport(result.Success, "", artifacts, logPath, result.ExitCode, result.TimedOut);
                log.AppendLine(configureOnly ? $"CMake 配置{(result.Success ? "成功" : "失败")}，退出代码：{result.ExitCode}" : report.Summary);
                await File.WriteAllTextAsync(logPath, log.ToString(), cancellationToken);
                if (result.Success && !configureOnly)
                    await BuildReceipt.WriteAsync(root, project, tools.Fingerprint,
                        project.Kind == ProjectKind.CubeMx ? cubeExecutables : [Path.Combine(build, sdcc ? "firmware.hex" : "firmware.bin")], cancellationToken,
                        sourceStamp == await Debugging.DebugSourceStamp.ComputeAsync(root, cancellationToken) ? sourceStamp : null);
                return report with { Log = log.ToString() };
            }
            // 不使用用户 PATH 发现工具；只为编译器的子程序提供本工具集和系统目录。
            var environment = ToolsetEnvironment.Create(tools);
            string CmakePath(string path) => path.Replace('\\', '/');
            List<string> configure = ["-S", root, "-B", build, "-G", "Ninja", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON",
                "-DCMAKE_MAKE_PROGRAM=" + CmakePath(tools.Tool("ninja"))];
            if (!sdcc) configure.Add("-DCMAKE_OBJCOPY=" + CmakePath(tools.Tool("objcopy")));
            if (project.CubeMx is { } cube)
            {
                if (cube.ConfigurePreset is { } preset) configure.InsertRange(0, ["--preset", preset]);
                else configure.Add("-DCMAKE_BUILD_TYPE=" + cube.BuildType);
                configure.Add("-DCMAKE_TOOLCHAIN_FILE=" + CmakePath(PathBoundary.Resolve(root, cube.ToolchainFile)));
                // CubeMX 工具链通过隔离 PATH 选择编译器，File API 再核对其归属。
                // 重复 -D 编译器路径会因 Windows 路径大小写不同触发 CMake 清缓存，丢失交叉编译配置。
                await CMakeFileApi.QueryAsync(build, cancellationToken);
            }
            else if (sdcc) configure.AddRange(["-DCMAKE_BUILD_TYPE=", "-DCMAKE_C_COMPILER=" + CmakePath(tools.Tool("sdcc")),
                "-DCMAKE_AR=" + CmakePath(tools.Tool("sdar"))]);
            else configure.AddRange(["-DCMAKE_BUILD_TYPE=Debug", "-DCMAKE_C_COMPILER=" + CmakePath(tools.Tool("gcc")),
                "-DCMAKE_CXX_COMPILER=" + CmakePath(tools.Tool("gxx")), "-DCMAKE_ASM_COMPILER=" + CmakePath(tools.Tool("gcc"))]);
            // IDE 或工程移动之后，清除 CMake 的机器相关缓存，源码和已有固件不受影响。
            var cacheIdentityPath = Path.Combine(build, "studiox-runtime.json");
            var identity = new BuildCacheIdentity(root, tools.RootDirectory, tools.Fingerprint, project.CubeMx,
                ConfigurationVersion: sdcc ? 3 : project.Kind == ProjectKind.CubeMx ? 1 : 0, BuildSettings: settings,
                StcClockConfiguration: stcIsp is null || stcIsp.ClockMode == StcClockMode.Preserve
                    ? null : $"{stcIsp.ClockMode}:{stcIsp.ClockFrequencyHz}");
            if (!File.Exists(cacheIdentityPath) || await JsonStore.ReadAsync<BuildCacheIdentity>(cacheIdentityPath, cancellationToken) != identity)
            {
                if (File.Exists(Path.Combine(build, "CMakeCache.txt")))
                {
                    const string message = "构建配置已变化或上次配置未完成，正在重建 CMake 缓存…";
                    progress?.Report(message);
                    log.AppendLine(message);
                }
                configure.Add("--fresh");
            }
            foreach (var role in new[] { "ar", "ranlib" })
                if (tools.Manifest.Executables.ContainsKey(role)) configure.Add("-DCMAKE_" + role.ToUpperInvariant() + "=" + CmakePath(tools.Tool(role)));
            if (settings.HasOverrides || stcClockHz is not null)
                configure.AddRange(["-C", await settings.PrepareCMakeAsync(build, project, cancellationToken, stcClockHz)]);
            ProcessResult? buildResult = null;
            foreach (var (phase, arguments) in new[] { ("配置 CMake", configure.ToArray()), ("编译固件", new[] { "--build", build, "--parallel", Math.Clamp(System.Environment.ProcessorCount / 2, 1, 8).ToString() }) })
            {
                progress?.Report(phase);
                // 仅成功配置可以复用缓存；失败、取消和 File API 校验失败都需要重新配置。
                if (phase == "配置 CMake" && File.Exists(cacheIdentityPath)) File.Delete(cacheIdentityPath);
                var result = await new ProcessRunner().RunAsync(new ProcessRequest(tools.Tool("cmake"), arguments, root, TimeSpan.FromMinutes(5), environment,
                    RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output), cancellationToken);
                log.AppendLine($"[{phase}] exit={result.ExitCode}, timeout={result.TimedOut}, truncated={result.OutputTruncated}");
                log.AppendLine(result.StandardOutput).AppendLine(result.StandardError);
                if (!result.Success) return await CompleteAsync(result, []);
                if (phase == "编译固件") buildResult = result;
                if (phase == "配置 CMake")
                {
                    try { await settings.VerifyCommandsAsync(build, project, cancellationToken, stcClockHz); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.AppendLine(ex.Message);
                        await File.WriteAllTextAsync(logPath, log.ToString(), cancellationToken);
                        throw;
                    }
                    if (project.Kind == ProjectKind.CubeMx) cubeExecutables = await CMakeFileApi.ExecutablesAsync(build, tools, cancellationToken);
                    await JsonStore.WriteAsync(cacheIdentityPath, identity, cancellationToken);
                    if (configureOnly) return await CompleteAsync(result, []);
                }
            }
            if (project.Kind == ProjectKind.CubeMx)
            {
                var importedArtifacts = new List<string>();
                foreach (var elf in cubeExecutables)
                {
                    if (!File.Exists(elf)) throw new StudioXException("BUILD_ARTIFACT", "工具报告成功，但固件文件缺失：" + elf);
                    importedArtifacts.Add(elf);
                    foreach (var (format, extension) in new[] { ("binary", ".bin"), ("ihex", ".hex") })
                    {
                        var artifact = Path.ChangeExtension(elf, extension);
                        if (artifact.Equals(elf, StringComparison.OrdinalIgnoreCase)) throw new StudioXException("BUILD_ARTIFACT", "固件目标扩展名与转换产物冲突。");
                        // 用户的 POST_BUILD 可能对映像做填充或签名，已有输出必须保留。
                        if (File.Exists(artifact)) { importedArtifacts.Add(artifact); continue; }
                        artifact = PathBoundary.Resolve(build, "studiox-artifacts/" + Path.GetRelativePath(build, artifact).Replace('\\', '/'));
                        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                        var conversion = await new ProcessRunner().RunAsync(new(tools.Tool("objcopy"), ["-O", format, elf, artifact], root,
                            TimeSpan.FromSeconds(30), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output), cancellationToken);
                        log.AppendLine("[转换固件] " + artifact).AppendLine(conversion.StandardOutput).AppendLine(conversion.StandardError);
                        if (!conversion.Success) return await CompleteAsync(conversion, []);
                        importedArtifacts.Add(artifact);
                    }
                    var map = Path.ChangeExtension(elf, ".map");
                    if (File.Exists(map)) importedArtifacts.Add(map);
                }
                var importedSize = await new ProcessRunner().RunAsync(new(tools.Tool("size"), cubeExecutables, root,
                    TimeSpan.FromSeconds(20), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output), cancellationToken);
                log.AppendLine("[固件大小]").AppendLine(importedSize.StandardOutput).AppendLine(importedSize.StandardError);
                return await CompleteAsync(importedSize, importedArtifacts.ToArray());
            }
            if (sdcc)
            {
                var required = new[] { "firmware.ihx", "firmware.hex", "firmware.map" }.Select(p => Path.Combine(build, p)).ToArray();
                if (required.Any(p => !File.Exists(p)))
                    throw new StudioXException("BUILD_ARTIFACT", "SDCC 报告成功，但 Intel HEX 或映射文件缺失。");
                await stcRom!.VerifyBuildAsync(build, settings.CodeRomSizeBytes, cancellationToken);
                var sdccArtifacts = required.Concat(new[] { Path.Combine(build, "firmware.mem") }.Where(File.Exists)).ToArray();
                return await CompleteAsync(buildResult!, sdccArtifacts);
            }
            var artifacts = new[] { "firmware.elf", "firmware.bin", "firmware.hex", "firmware.map" }.Select(p => Path.Combine(build, p)).ToArray();
            if (artifacts.Any(p => !File.Exists(p))) throw new StudioXException("BUILD_ARTIFACT", "工具报告成功，但部分固件文件缺失。");
            var size = await new ProcessRunner().RunAsync(new ProcessRequest(tools.Tool("size"), [artifacts[0]], root, TimeSpan.FromSeconds(20), environment,
                RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output), cancellationToken);
            log.AppendLine("[固件大小]").AppendLine(size.StandardOutput).AppendLine(size.StandardError);
            return await CompleteAsync(size, artifacts);
        }
        finally { gate.Release(); }
    }
    private sealed record BuildCacheIdentity(string ProjectDirectory, string ToolsetDirectory, string Fingerprint,
        CubeMxProjectSettings? CubeMx = null, int ConfigurationVersion = 0, ProjectBuildSettings? BuildSettings = null,
        string? StcClockConfiguration = null);
}
