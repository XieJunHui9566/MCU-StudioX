namespace StudioX.Engine.Zephyr;

using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>用锁定的 Zephyr、Python 和 SDK 构建项目自带的板级目标，不调用裸机后端或硬件工具。</summary>
public sealed class ZephyrBuildService(string userDataDirectory)
{
    public static void Invalidate(string root)
    {
        var published = PathBoundary.Resolve(root, ".build/zephyr");
        if (Directory.Exists(published))
        {
            foreach (var file in Directory.EnumerateFiles(published).Where(p => Path.GetExtension(p) is ".elf" or ".bin" or ".hex" or ".map"))
            {
                File.Delete(file);
            }
        }
        var commands = PathBoundary.Resolve(root, ".build/compile_commands.json");
        if (File.Exists(commands))
        {
            File.Delete(commands);
        }
    }

    public async Task<BuildReport> ExecuteAsync(string root, ProjectManifest project, ResolvedToolset tools,
        bool configureOnly, IProgress<string>? output, CancellationToken token)
    {
        if (project.Zephyr is not { FormatVersion: 2 } settings)
        {
            throw ZephyrProjectService.RuntimeUnavailable();
        }
        if (tools.Manifest.Purpose != "zephyr" || tools.Manifest.ComponentVersions?.GetValueOrDefault("zephyr") != settings.ZephyrVersion ||
            tools.Manifest.ComponentVersions?.GetValueOrDefault("sdk") != "1.0.1")
        {
            throw new StudioXException("ZEPHYR_RUNTIME", "开发组件的 Zephyr / SDK 身份与工程锁定版本不符。");
        }
        var zephyr = ZephyrNativePath.Resolve(tools.ResourceDirectory("zephyr"));
        var sdk = ZephyrNativePath.Resolve(tools.ResourceDirectory("sdk"));
        var pythonHome = ZephyrNativePath.Resolve(tools.ResourceDirectory("python"));
        var modules = new[] { "cmsis", "cmsis6", "halStm32" }.Select(role => ZephyrNativePath.Resolve(tools.ResourceDirectory(role))).ToArray();
        var profile = ZephyrDeviceCatalog.Require(project.DeviceId);
        foreach (var (relative, expected) in profile.Evidence)
        {
            await using var input = File.OpenRead(PathBoundary.Resolve(tools.ResourceDirectory("zephyr"), relative));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("ZEPHYR_SOC_CHANGED", "Zephyr SoC 配置与模板核验记录不符：" + relative);
            }
        }

        Invalidate(root);
        var published = PathBoundary.Resolve(root, ".build/zephyr");
        Directory.CreateDirectory(published);
        var logPath = Path.Combine(published, "build.log");
        var log = new StringBuilder();
        // Windows 版 binutils 仍有传统路径长度约束；缩短目录并优先使用原生短路径。
        var cache = Path.Combine(userDataDirectory, "zephyr", "r", Guid.NewGuid().ToString("N")[..16]);
        if (Directory.Exists(cache) || File.Exists(cache))
        {
            throw new StudioXException("ZEPHYR_CACHE_EXISTS", "构建临时目录已存在，请重新编译。");
        }
        var source = Path.Combine(cache, "src");
        var build = Path.Combine(cache, "build");
        Directory.CreateDirectory(source);
        try
        {
            var stamp = await ZephyrSourceSnapshot.HashAsync(root, token);
            foreach (var relative in ZephyrSourceSnapshot.Files(root))
            {
                token.ThrowIfCancellationRequested();
                var destination = PathBoundary.Resolve(source, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(PathBoundary.Resolve(root, relative), destination);
            }
            if (stamp != await ZephyrSourceSnapshot.HashAsync(source, token) || stamp != await ZephyrSourceSnapshot.HashAsync(root, token))
            {
                throw new StudioXException("ZEPHYR_SOURCE_CHANGED", "准备构建快照期间工程发生变化，请重新编译。");
            }
            var nativeSource = ZephyrNativePath.Resolve(source);
            Directory.CreateDirectory(build);
            var nativeBuild = ZephyrNativePath.Resolve(build);
            var compatibility = await ZephyrCmakeCompatibility.WriteAsync(cache, token);
            string Display(string value) => value.Replace(nativeSource, root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                .Replace(source.Replace('\\', '/'), root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
                .Replace(source, root, StringComparison.OrdinalIgnoreCase);
            var environment = ToolsetEnvironment.Create(tools);
            environment["PATH"] = string.Join(Path.PathSeparator, new[] { Path.GetDirectoryName(tools.Tool("cmake"))!, Path.GetDirectoryName(tools.Tool("ninja"))!,
                pythonHome, sdk + "/gnu/arm-zephyr-eabi/bin", Environment.GetFolderPath(Environment.SpecialFolder.System) }.Select(ZephyrNativePath.Resolve));
            environment["ZEPHYR_BASE"] = zephyr;
            environment["ZEPHYR_SDK_INSTALL_DIR"] = sdk;
            environment["ZEPHYR_TOOLCHAIN_VARIANT"] = "zephyr";
            environment["PYTHONHOME"] = pythonHome;
            environment["PYTHONPATH"] = zephyr + "/scripts";
            environment["PYTHONPYCACHEPREFIX"] = Path.Combine(cache, "pycache");
            var removed = ToolsetEnvironment.AmbientVariables.Concat(new[] { "BOARD", "BOARD_ROOT", "CONF_FILE", "EXTRA_CONF_FILE", "DTC_OVERLAY_FILE",
                "EXTRA_DTC_OVERLAY_FILE", "ZEPHYR_MODULES", "ZEPHYR_EXTRA_MODULES", "ZEPHYR_BASE", "ZEPHYR_SDK_INSTALL_DIR",
                "ZEPHYR_TOOLCHAIN_VARIANT", "WEST_CONFIG_LOCAL", "SOC_ROOT", "ARCH_ROOT", "DTS_ROOT", "TOOLCHAIN_ROOT", "SHIELD", "SNIPPET" }).ToArray();
            async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments)
            {
                var capture = new ToolOutput(text =>
                {
                    lock (log)
                    {
                        log.Append(text);
                    }
                    output?.Report(Display(text));
                });
                var result = await new ProcessRunner().RunAsync(new(tools.Tool("cmake"), arguments, source, TimeSpan.FromMinutes(10),
                    environment, RemoveEnvironment: removed, Output: capture, StreamCompleteOutput: true), token);
                return result;
            }
            output?.Report($"Zephyr {settings.ZephyrVersion} · {project.DeviceId} · 自定义板 {settings.BoardTarget}");
            var result = await RunAsync(["-S", nativeSource, "-B", nativeBuild, "-G", "Ninja", "-DBOARD=" + settings.BoardTarget,
                "-DZEPHYR_BASE=" + zephyr, "-DZephyr_DIR=" + zephyr + "/share/zephyr-package/cmake",
                "-DCMAKE_FIND_USE_PACKAGE_REGISTRY=OFF", "-DCMAKE_FIND_USE_SYSTEM_PACKAGE_REGISTRY=OFF",
                "-DZEPHYR_MODULES=" + string.Join(';', modules), "-DZEPHYR_SDK_INSTALL_DIR=" + sdk,
                "-DZEPHYR_TOOLCHAIN_VARIANT=zephyr", "-DPython3_EXECUTABLE=" + pythonHome + "/python.exe",
                "-DPYTHON_EXECUTABLE=" + pythonHome + "/python.exe", "-DCMAKE_MAKE_PROGRAM=" + ZephyrNativePath.Resolve(tools.Tool("ninja")),
                "-DCMAKE_PROJECT_Zephyr-Kernel_INCLUDE=" + compatibility,
                "-DUSER_CACHE_DIR=" + nativeBuild + "/cache", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"]);
            if (result.Success && !configureOnly)
            {
                result = await RunAsync(["--build", nativeBuild, "--parallel", Math.Min(Environment.ProcessorCount, 8).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            }
            if (await ProjectService.ReadAsync(root, token) != project || stamp != await ZephyrSourceSnapshot.HashAsync(root, token))
            {
                throw new StudioXException("ZEPHYR_SOURCE_CHANGED", "构建期间工程或板级配置发生变化，未发布构建产物，请重新编译。");
            }
            var artifacts = new List<string>();
            if (result.Success)
            {
                await ZephyrAnalysisExporter.ExportAsync(root, source, build, token);
                foreach (var name in new[] { "zephyr.dts", ".config", "zephyr.elf", "zephyr.bin", "zephyr.hex", "zephyr.map" })
                {
                    if (configureOnly && name is not "zephyr.dts" and not ".config")
                    {
                        continue;
                    }
                    var from = Path.Combine(build, "zephyr", name);
                    if (File.Exists(from))
                    {
                        var to = Path.Combine(published, name);
                        File.Copy(from, to, true);
                        artifacts.Add(to);
                    }
                }
                if (!configureOnly && !artifacts.Any(p => p.EndsWith(".elf", StringComparison.Ordinal)))
                {
                    throw new StudioXException("ZEPHYR_ARTIFACT", "Zephyr 构建未生成 zephyr.elf，请检查原始构建日志。");
                }
            }
            var mappedLog = Display(log.ToString());
            return new(result.Success, mappedLog, artifacts, logPath, result.ExitCode, result.TimedOut);
        }
        catch (Exception error)
        {
            Invalidate(root);
            log.AppendLine(error.ToString());
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(logPath, log.ToString(), CancellationToken.None);
            // 缓存目录由本次调用创建，删除前不使用来自工程的路径。
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
            }
        }
    }

    private sealed class ToolOutput(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
