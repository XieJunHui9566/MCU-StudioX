namespace StudioX.Engine;

using System.Text;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>使用 SDK 的原生构建入口，保留应用、引导程序和分区表之间的关系。</summary>
internal sealed class EspressifBuildBackend
{
    internal async Task<BuildReport> ExecuteAsync(string root, ProjectManifest project, ResolvedToolset tools,
        ProjectBuildSettings settings, bool configureOnly, IProgress<string>? progress, IProgress<string>? output,
        CancellationToken token)
    {
        var sdk = project.Espressif ?? throw new StudioXException("ESPRESSIF_PROJECT", "工程没有锁定 Espressif SDK。");
        var build = PathBoundary.Resolve(root, ".build");
        Directory.CreateDirectory(build);
        var log = new StringBuilder();
        var logGate = new object();
        var logPath = PathBoundary.Resolve(build, "studiox-build.log");
        var cachePath = PathBoundary.Resolve(build, "studiox-idf-runtime.json");
        var streamed = new ToolOutput(text =>
        {
            lock (logGate)
            {
                log.Append(text);
            }
            output?.Report(text);
        });
        try
        {
            log.AppendLine($"{sdk.Framework} {sdk.SdkVersion} · target={sdk.Target}");
            log.AppendLine("工程编译参数：" + settings.SummaryFor(project));
            var module = await EspressifModuleConfiguration.ReadAsync(root, token);
            var identity = new CacheIdentity(root, tools.RootDirectory, tools.Fingerprint, sdk, settings, module.Settings, EspressifNativeTools.Revision);
            var moduleBase = await EspressifModuleSdkConfig.PrepareAsync(root, module, token);
            if (moduleBase is not null)
            {
                log.AppendLine("模块配置保留原生选项：" + Path.GetRelativePath(root, moduleBase));
            }
            var environment = await EspressifBuildEnvironment.CreateAsync(root, tools, sdk, token);
            var nativeRoot = EspressifNativePath.For(root);
            var nativeBuild = EspressifNativePath.For(build);
            var python = EspressifNativePath.ForExecutable(tools.Tool("python"));
            var frontend = PathBoundary.Resolve(environment["IDF_PATH"], "tools/idf.py");
            var configure = new List<string> { frontend, "-C", nativeRoot, "-B", nativeBuild, "-G", "Ninja", "-D", "IDF_TARGET=" + sdk.Target,
                "-D", "CMAKE_EXPORT_COMPILE_COMMANDS=ON", "-D", "CMAKE_MAKE_PROGRAM=" + CmakePath(EspressifNativePath.For(tools.Tool("ninja"))),
                "-D", "PYTHON=" + CmakePath(python) };
            configure.AddRange(await EspressifNativeTools.PrepareAsync(build, tools, sdk, token));
            if (module.Settings.HasOverrides)
            {
                configure.AddRange(["-D", "SDKCONFIG=" + CmakePath(Path.Combine(nativeBuild, "studiox-module-sdkconfig"))]);
            }
            if (settings.HasOverrides)
            {
                await settings.PrepareCMakeAsync(build, project, token);
                configure.AddRange(["-D", "CMAKE_PROJECT_INCLUDE=" + CmakePath(EspressifNativePath.For(PathBoundary.Resolve(build, "studiox-compiler-options.cmake")))]);
            }
            if (module.Settings.HasOverrides)
            {
                var hook = PathBoundary.Resolve(build, "studiox-module-hardware.cmake");
                var psram = module.Settings.PsramMode is "quad" or "octal" or "hex";
                // 锁定 IDF 的 project() 先计算 components，再调用原生 project()，最后处理 SDK 配置与组件闭包。
                // 此处追加已存在的 SDK 组件，保留用户计算出的组件根；不能改用户的 COMPONENTS 或模板文件。
                var script = "if(NOT BOOTLOADER_BUILD AND CMAKE_CURRENT_SOURCE_DIR STREQUAL CMAKE_SOURCE_DIR)\n" +
                    "  execute_process(COMMAND \"${PYTHON}\" -c \"import os,sys; a,b,c=(os.path.normcase(os.path.realpath(x)) for x in sys.argv[1:]); sys.exit(0 if a in (b,c) else 1)\"\n" +
                    "    \"${sdkconfig}\" \"" + CmakePath(EspressifNativePath.For(moduleBase!)) + "\" \"${CMAKE_BINARY_DIR}/studiox-module-sdkconfig\"\n" +
                    "    RESULT_VARIABLE _studiox_base_match)\n" +
                    "  if(NOT _studiox_base_match EQUAL 0)\n" +
                    "    message(FATAL_ERROR \"MCU StudioX: custom SDKCONFIG has not been configured as the module base. Save the native SDK settings selection and build once before choosing a module, or configure once with the native SDK; user configuration remains unchanged.\")\n" +
                    "  endif()\n" +
                    "  set(SDKCONFIG \"${CMAKE_BINARY_DIR}/studiox-module-sdkconfig\")\n" +
                    "  set(sdkconfig \"${CMAKE_BINARY_DIR}/studiox-module-sdkconfig\")\n" +
                    (psram ? "  list(APPEND components esp_psram)\n  list(REMOVE_DUPLICATES components)\n" : "") + "endif()\n" +
                    (settings.HasOverrides ? "include(\"${CMAKE_CURRENT_LIST_DIR}/studiox-compiler-options.cmake\")\n" : "");
                await File.WriteAllTextAsync(hook, script, token);
                configure.AddRange(["-D", "CMAKE_PROJECT_INCLUDE=" + CmakePath(EspressifNativePath.For(hook))]);
            }
            if (!File.Exists(cachePath) || await JsonStore.ReadAsync<CacheIdentity>(cachePath, token) != identity)
            {
                progress?.Report("构建环境已变化，重建原生 CMake 缓存…");
                log.AppendLine("构建环境已变化，重建原生 CMake 缓存；保留源码与 sdkconfig。");
                var parameterBackup = await BackupParameterCachesAsync(root, token);
                if (parameterBackup is not null)
                {
                    log.AppendLine("原生参数缓存已备份：" + parameterBackup);
                }
                ClearCMakeCache(build, token);
            }
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
            await CMakeFileApi.QueryAsync(build, token);
            configure.Add("reconfigure");
            progress?.Report("配置 " + sdk.Framework);
            var configured = await RunAsync(python, configure, "配置 IDF", nativeRoot, environment, log, logGate, streamed, token);
            if (!configured.Success)
            {
                return Report(configured, []);
            }
            // 原生配置可能首次生成 sdkconfig 或组件锁；配置完成后才记录本次编译的源码快照。
            await settings.VerifyCommandsAsync(build, project, token);
            _ = await EspressifBuildArtifacts.ReadAsync(root, tools, sdk, false, token);
            await EspressifModuleSdkConfig.VerifyAsync(root, module, null, token);
            await EspressifBuildInputs.ValidateConfigurationAsync(root, tools, sdk, token);
            await JsonStore.WriteAsync(cachePath, identity, token);
            if (configureOnly)
            {
                return Report(configured, []);
            }
            var sourceStamp = await DebugSourceStamp.ComputeAsync(root, token);
            progress?.Report("编译 " + sdk.Target + " 固件");
            var compiled = await RunAsync(python, [frontend, "-C", nativeRoot, "-B", nativeBuild, "build"], "编译 IDF",
                nativeRoot, environment, log, logGate, streamed, token);
            if (!compiled.Success)
            {
                return Report(compiled, []);
            }
            var artifacts = await EspressifBuildArtifacts.ReadAsync(root, tools, sdk, true, token);
            await EspressifBuildInputs.ValidateDependenciesAsync(root, tools, environment, token);
            var layout = await EspressifFlashLayoutReader.ParseAsync(root, ".build/flasher_args.json", sdk.Target, module.DeclaredFlashBytes, token);
            await EspressifModuleSdkConfig.VerifyAsync(root, module, layout, token);
            await BuildReceipt.WriteEspressifAsync(root, project, tools.Fingerprint, layout, artifacts.ApplicationBin,
                artifacts.ApplicationElf, token, sourceStamp == await DebugSourceStamp.ComputeAsync(root, token) ? sourceStamp : null);
            token.ThrowIfCancellationRequested();
            var paths = artifacts.Artifacts.Concat(layout.Images.Select(image => PathBoundary.Resolve(root, image.RelativePath)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return Report(compiled, paths);
        }
        catch (Exception exception)
        {
            lock (logGate)
            {
                log.AppendLine().AppendLine(exception.ToString());
            }
            InvalidateReceipt(root);
            throw;
        }
        finally
        {
            // 取消同样保留原始诊断；删除凭据已由统一构建入口在启动前完成。
            string text;
            lock (logGate)
            {
                text = log.ToString();
            }
            try
            {
                await File.WriteAllTextAsync(logPath, text, CancellationToken.None);
            }
            catch
            {
                InvalidateReceipt(root);
                throw;
            }
        }

        BuildReport Report(ProcessResult result, string[] artifacts)
        {
            lock (logGate)
            {
                log.AppendLine(configureOnly ? $"IDF 配置{(result.Success ? "成功" : "失败")}，退出代码：{result.ExitCode}"
                    : $"IDF 编译{(result.Success ? "成功" : "失败")}，退出代码：{result.ExitCode}");
                return new(result.Success, log.ToString(), artifacts, logPath, result.ExitCode, result.TimedOut);
            }
        }
    }

    private static async Task<ProcessResult> RunAsync(string python, IReadOnlyList<string> arguments, string phase,
        string root, Dictionary<string, string> environment, StringBuilder log, object logGate,
        IProgress<string> output, CancellationToken token)
    {
        lock (logGate)
        {
            log.AppendLine("[" + phase + "]");
        }
        var result = await new ProcessRunner().RunAsync(new(python, arguments, root, TimeSpan.FromMinutes(20),
            environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables, Output: output), token);
        lock (logGate)
        {
            log.AppendLine($"[{phase}] exit={result.ExitCode}, timeout={result.TimedOut}, truncated={result.OutputTruncated}");
        }
        return result;
    }

    private static void ClearCMakeCache(string build, CancellationToken token)
    {
        var directories = new List<string>();
        var caches = new List<string>();
        var pending = new Stack<string>();
        pending.Push(build);
        while (pending.TryPop(out var directory))
        {
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                var owned = PathBoundary.Resolve(build, Path.GetRelativePath(build, item.FullName).Replace('\\', '/'));
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("ESPRESSIF_CACHE", "构建缓存含有目录别名，不能自动清理：" + item.FullName);
                }
                if (item is DirectoryInfo)
                {
                    // 备份是恢复证据，不是当前构建输入；不能递归清掉此前健康检查保存的 CMake 缓存。
                    if (item.Name == ".studiox-cache-backups")
                    {
                        continue;
                    }
                    pending.Push(owned);
                    if (item.Name == "CMakeFiles" || item.Name.EndsWith("-stamp", StringComparison.Ordinal))
                    {
                        directories.Add(owned);
                    }
                }
                else if (item.Name == "CMakeCache.txt")
                {
                    caches.Add(owned);
                }
            }
        }
        // 引导程序和其他原生外部项目也带机器相关缓存；只清缓存与步骤记录，保留 SDK 配置和现有映像。
        foreach (var directory in directories.OrderByDescending(path => path.Length))
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        foreach (var cache in caches)
        {
            if (File.Exists(cache))
            {
                File.Delete(cache);
            }
        }
    }

    private static string CmakePath(string value) => value.Replace('\\', '/');
    private static void InvalidateReceipt(string root)
    {
        var receipt = PathBoundary.Resolve(root, BuildReceipt.RelativePath);
        if (File.Exists(receipt))
        {
            File.Delete(receipt);
        }
    }
    private static async Task<string?> BackupParameterCachesAsync(string root, CancellationToken token)
    {
        var entries = BuildService.EspressifParameterCaches.Select(relative => BuildService.CacheEntry(root, relative, token))
            .OfType<ConfigurationCacheEntry>().ToArray();
        if (entries.Length == 0)
        {
            return null;
        }
        var backup = PathBoundary.Resolve(root, ".build/.studiox-cache-backups/idf-parameters-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var moved = new List<(string Source, string Destination, bool Directory)>();
        try
        {
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                var source = PathBoundary.Resolve(root, entry.RelativePath);
                var destination = PathBoundary.Resolve(backup, entry.RelativePath[".build/".Length..]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (entry.Directory)
                {
                    Directory.Move(source, destination);
                }
                else
                {
                    File.Move(source, destination);
                }
                moved.Add((source, destination, entry.Directory));
            }
            await JsonStore.WriteAsync(PathBoundary.Resolve(backup, "cache-backup.json"), new ConfigurationCachePlan(root, entries), token);
            return backup;
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (var entry in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (entry.Directory)
                    {
                        Directory.Move(entry.Destination, entry.Source);
                    }
                    else
                    {
                        File.Move(entry.Destination, entry.Source);
                    }
                }
                catch (Exception restore) { errors.Add(restore); }
            }
            if (errors.Count > 1)
            {
                throw new AggregateException("原生参数缓存备份失败且部分回退失败，文件保留在备份目录。", errors);
            }
            throw;
        }
    }

    private sealed record CacheIdentity(string ProjectDirectory, string ToolsetDirectory, string Fingerprint,
        EspressifProjectSettings Sdk, ProjectBuildSettings BuildSettings, EspressifModuleSettings ModuleSettings, int NativeToolsRevision);
    private sealed class ToolOutput(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
