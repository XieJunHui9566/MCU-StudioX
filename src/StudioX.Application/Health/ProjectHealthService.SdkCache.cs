namespace StudioX.Application.Health;

using System.Text;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class ProjectHealthService
{
    private static async Task InspectSdkAsync(string root, ProjectManifest project, ResolvedToolset tools, List<HealthCheck> checks, CancellationToken token)
    {
        try
        {
            var settings = project.Espressif!;
            if (tools.Manifest.Purpose != settings.Framework || EspressifSdkIdentity.DeclaredVersion(tools.Manifest) != settings.SdkVersion)
            {
                throw new StudioXException("ESPRESSIF_TOOLSET", "SDK 用途或版本与工程锁定不一致。");
            }
            foreach (var role in new[] { "idf", "tools", "python-env" })
            {
                if (!Directory.Exists(tools.ResourceDirectory(role)))
                {
                    throw new StudioXException("TOOL_RESOURCE", "SDK 资源目录缺失：" + role);
                }
            }
            var sdkRoot = tools.ResourceDirectory("idf");
            foreach (var relative in new[] { "tools/idf.py", "tools/cmake/project.cmake" })
            {
                if (!File.Exists(PathBoundary.Resolve(sdkRoot, relative)))
                {
                    throw new StudioXException("TOOL_RESOURCE", "缺少原生 SDK 入口：" + relative);
                }
            }
            checks.Add(new("HEALTH_SDK", "SDK 身份与入口", HealthState.Passed,
                $"{settings.Framework} / {settings.SdkVersion} · target={settings.Target}\nSDK：{sdkRoot}\n版本来源为工程与工具清单；快速检查未校验 SDK 全部内容。", "esp-idf", HealthAction.SdkSettings));
            var config = PathBoundary.Resolve(root, "sdkconfig");
            if (File.Exists(config))
            {
                var text = Encoding.UTF8.GetString(await ReadLimitedAsync(config, 8 * 1024 * 1024, token));
                var line = text.Split('\n').FirstOrDefault(line => line.TrimStart().StartsWith("CONFIG_IDF_TARGET=", StringComparison.Ordinal));
                if (line is not null && line.Split('=', 2)[1].Trim().Trim('"') != settings.Target)
                {
                    checks.Add(new("HEALTH_SDK_CONFIG_TARGET", "sdkconfig 的目标需要核对", HealthState.Warning,
                    $"根 sdkconfig：{line.Trim()}；工程 target={settings.Target}。若选择了其他原生配置文件，以原生 SDK 设置为准；请核对后再构建，不自动删除 sdkconfig。", "esp-idf-errors", HealthAction.SdkSettings, line.Trim()));
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { checks.Add(Failure("HEALTH_SDK", "SDK 身份与入口", "esp-idf-errors", HealthAction.Tools, error, project.ToolsetId, project.ToolsetVersion)); }
    }

    private static async Task InspectCacheAsync(string root, ProjectManifest project, ResolvedToolset? tools, List<HealthCheck> checks, CancellationToken token)
    {
        try
        {
            var cache = PathBoundary.Resolve(root, ".build/CMakeCache.txt");
            if (!File.Exists(cache))
            {
                checks.Add(new("HEALTH_CACHE_NEW", "CMake 配置缓存", HealthState.Information, "尚无配置缓存，首次构建会自动配置；无需修复。", "build"));
                return;
            }
            var text = Encoding.UTF8.GetString(await ReadLimitedAsync(cache, 8 * 1024 * 1024, token));
            var values = text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("//", StringComparison.Ordinal))
                .Select(line => line.Split('=', 2)).Where(parts => parts.Length == 2).GroupBy(parts => parts[0].Split(':', 2)[0], StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.Ordinal);
            var issues = false;
            if (values.TryGetValue("CMAKE_HOME_DIRECTORY", out var home) && !SamePath(home, root))
            {
                checks.Add(new("HEALTH_CACHE_PROJECT", "缓存属于其他工程目录", HealthState.Error,
                    "工程可能被复制或移动。确认后仅将已知 CMake 生成缓存移入备份，再由下次构建重新配置。", "build-errors", HealthAction.ResetCache,
                    "CMAKE_HOME_DIRECTORY=" + home + "\n当前目录=" + root));
                issues = true;
            }
            if (values.TryGetValue("CMAKE_C_COMPILER", out var compiler))
            {
                if (!Path.IsPathRooted(compiler) || !File.Exists(compiler))
                {
                    checks.Add(new("HEALTH_CACHE_COMPILER", "缓存中的编译器不可用", HealthState.Warning,
                        "缓存记录的编译器路径已失效。构建使用工程锁定的内置工具；可预览并重建配置缓存。", "tool-environment", HealthAction.ResetCache, "CMAKE_C_COMPILER=" + compiler));
                    issues = true;
                }
                else if (tools is not null && tools.Manifest.Executables.ContainsKey("gcc") &&
                    !(project.Espressif is { } esp ? AnalysisEnvironmentInspector.MatchesCompiler(compiler, tools, esp) : SamePath(compiler, tools.Tool("gcc"))))
                {
                    checks.Add(new("HEALTH_CACHE_TOOLSET", "缓存与锁定编译器不同", HealthState.Warning,
                        "旧缓存可能来自其他开发环境组件。请核对并重建配置缓存，工程工具锁保持不变。", "tool-environment", HealthAction.ResetCache,
                        "缓存=" + compiler + "\n锁定=" + tools.Tool("gcc")));
                    issues = true;
                }
                if (project.Espressif is not null && Path.GetFileName(compiler).Contains('~'))
                {
                    checks.Add(new("HEALTH_XTENSA_ALIAS", "Espressif 编译器使用短文件名", HealthState.Warning,
                        "短文件名可能触发 Xtensa 动态配置冲突。目录短路径可继续使用；编译器文件名需完整，由当前构建引擎配置。", "esp-idf-errors", HealthAction.ResetCache, compiler));
                    issues = true;
                }
            }
            if (values.TryGetValue("IDF_TARGET", out var cachedTarget) && project.Espressif is { } sdk && cachedTarget != sdk.Target)
            {
                checks.Add(new("HEALTH_CACHE_TARGET", "缓存与工程 SDK target 不同", HealthState.Warning,
                    $"缓存 target={cachedTarget}；工程 target={sdk.Target}。缓存修复保留 sdkconfig，原生配置的目标仍需单独核对。", "esp-idf-errors", HealthAction.ResetCache));
                issues = true;
            }
            if (!issues)
            {
                checks.Add(new("HEALTH_CACHE", "CMake 缓存身份", HealthState.Information,
                "未发现已有缓存的目录、编译器或 target 冲突。原生构建仍会核对完整环境与配置，不把缓存存在当作构建成功。", "build", HealthAction.ResetCache));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { checks.Add(Failure("HEALTH_CACHE", "配置缓存无法检查", "build-errors", HealthAction.ResetCache, error)); }
    }
    private static bool SamePath(string first, string second) => EspressifPathIdentity.NormalizePath(first).Equals(
        EspressifPathIdentity.NormalizePath(second), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
