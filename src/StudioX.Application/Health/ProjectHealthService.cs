namespace StudioX.Application.Health;

using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>只读检查当前工程的明确配置；快速检查不遍历 SDK，不把入口存在当作内容校验。</summary>
public sealed partial class ProjectHealthService(ToolsetCatalog catalog, BuildService builds, Func<bool>? debugActive = null)
{
    public Task<ProjectHealthReport> InspectAsync(string? directory, bool deep = false, IProgress<string>? progress = null,
        CancellationToken token = default) => Task.Run(() => InspectCoreAsync(directory, deep, progress, token), token);

    private async Task<ProjectHealthReport> InspectCoreAsync(string? directory, bool deep, IProgress<string>? progress, CancellationToken token)
    {
        var checks = new List<HealthCheck>();
        if (string.IsNullOrWhiteSpace(directory))
        {
            checks.Add(new("HEALTH_NO_PROJECT", "选择需要检查的工程", HealthState.Information,
                "先打开工程，或使用“选择工程检查”。检查也能读取尚未成功打开的 StudioX 工程目录。", "project-open"));
            return new(null, "未选择工程", "未选择目标", DateTimeOffset.UtcNow, deep, false, checks);
        }
        string root;
        ProjectManifest project;
        try
        {
            root = Path.GetFullPath(directory);
            progress?.Report("核对工程清单…");
            _ = await ReadLimitedAsync(PathBoundary.Resolve(root, ".studiox/project.json"), 1024 * 1024, token);
            project = await ProjectService.ReadAsync(root, token);
            checks.Add(new("HEALTH_PROJECT", "工程身份与格式", HealthState.Passed,
                $"{project.Name} · {project.Kind} · {project.DeviceId}\n器件包：{project.PackId} / {project.PackVersion}\n模板：{project.TemplateId}", "project-open"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            checks.Add(Failure("HEALTH_PROJECT", "工程清单无法读取或不一致", "project-open", HealthAction.None, error));
            return new(directory, "无法读取工程", "无法读取", DateTimeOffset.UtcNow, deep, false, checks);
        }
        var native = project.Kind is ProjectKind.Pack or ProjectKind.CubeMx;
        var target = project.Espressif is { } sdk ? $"{sdk.Framework} {sdk.SdkVersion} · target={sdk.Target}" : project.DeviceId;
        if (!native)
        {
            checks.Add(new("HEALTH_BUILD_MODE", "当前工程的构建方式", HealthState.Information,
                project.Kind == ProjectKind.MicroPython ? "MicroPython 是脚本工程，不需要原生 GCC/CMake 编译；运行入口使用 MicroPython 面板。"
                    : "Zephyr 当前为实验工程支持，尚未开放本 IDE 的原生固件构建入口。", project.Kind == ProjectKind.MicroPython ? "micropython" : "build"));
            return Report();
        }
        await CheckAsync("HEALTH_CMAKE", "工程构建入口", "project-layout", HealthAction.CMake, async () =>
        {
            var cmake = PathBoundary.Resolve(root, "CMakeLists.txt");
            var text = await ReadLimitedAsync(cmake, 8 * 1024 * 1024, token);
            if (text.Length == 0)
            {
                throw new StudioXException("HEALTH_CMAKE_EMPTY", "根 CMakeLists.txt 为空，请保留现有工程并修复构建入口。");
            }
            if (project.Kind == ProjectKind.Pack)
            {
                var manifest = await JsonStore.ReadAsync<PackManifest>(PathBoundary.Resolve(root, "device/manifest.json"), token);
                if (manifest.Id != project.PackId || manifest.Version != project.PackVersion || !manifest.Devices.Any(device => device.Id == project.DeviceId))
                {
                    throw new StudioXException("HEALTH_DEVICE", "工程内的器件清单与锁定的器件包或目标不一致。");
                }
                if (project.Espressif is { } sdk && manifest.Devices.Single(device => device.Id == project.DeviceId).Espressif != new EspressifDeviceDefinition(sdk.Framework, sdk.Target, sdk.SdkVersion))
                {
                    throw new StudioXException("ESPRESSIF_PROJECT", "工程与器件清单的 SDK 或 target 不一致。");
                }
                if (project.Espressif is null)
                {
                    foreach (var file in new[] { "device/CMakeLists.txt", "device/platform.cmake" })
                    {
                        if (!File.Exists(PathBoundary.Resolve(root, file)))
                        {
                            throw new StudioXException("HEALTH_CMAKE_GENERATED", "缺少生成的构建入口：" + file);
                        }
                    }
                }
            }
            return "根 CMakeLists.txt 与当前工程的必要生成入口存在。此检查不执行 CMake，也不保证用户 CMake 语法正确。";
        });
        await CheckAsync("HEALTH_BUILD_SETTINGS", "编译参数", "build-settings", HealthAction.BuildSettings, async () =>
        {
            var settings = await ProjectBuildSettings.ReadAsync(root, token);
            settings.ValidateFor(project);
            return settings.Summary;
        });
        ResolvedToolset? primary = null;
        await CheckAsync("HEALTH_COMPONENT_REQUIREMENTS", "工程开发环境组件需求与内容锁", "tool-environment", HealthAction.Tools, async () =>
        {
            var needs = await ProjectDevelopmentComponents.ReadAsync(root, project, token);
            var pins = await ProjectDevelopmentComponents.ReadPinsAsync(root, needs, token);
            foreach (var need in needs)
            {
                var tools = await InspectToolsAsync(need.Id, need.Version, need.CompilerId,
                    need.Id == project.ToolsetId ? project.Espressif?.Target : null, deep, checks, progress, token);
                if (need.Id == project.ToolsetId)
                {
                    primary = tools;
                }
                if (tools is not null)
                {
                    try
                    {
                        ProjectDevelopmentComponents.CheckFingerprint(pins, need.Id, tools.Fingerprint);
                    }
                    catch (Exception error)
                    {
                        checks.Add(Failure("HEALTH_COMPONENT_LOCK", "开发环境组件内容锁：" + need.Id,
                        "tool-environment", HealthAction.Tools, error, need.Id, need.Version));
                    }
                }
            }
            return $"工程声明 {needs.Count} 个精确版本的开发环境组件。" + (File.Exists(PathBoundary.Resolve(root, DevelopmentComponentLock.RelativePath))
                ? "已读取内容锁；检查不改写锁定文件。" : "完整组件内容锁将在首次构建校验通过后建立。");
        });
        if (primary is not null)
        {
            await CheckAsync("HEALTH_TOOL_LOCK", "工程工具内容锁定", "tool-environment", HealthAction.Tools, async () =>
            {
                var path = PathBoundary.Resolve(root, ".studiox/toolchain.lock.json");
                if (!File.Exists(path))
                {
                    return "尚未生成工具内容锁；首次构建会建立。健康检查不创建或修改锁定文件。";
                }
                var locked = await JsonStore.ReadAsync<ToolchainLock>(path, token);
                if (locked.FormatVersion != 1 || locked.ToolsetId != project.ToolsetId || locked.ToolsetVersion != project.ToolsetVersion
                    || !primary.Fingerprint.Equals(locked.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("TOOLCHAIN_LOCK", "已安装工具清单与工程锁定指纹不同。请恢复相同版本、相同内容的开发环境组件；不要删除工程锁来绕过检查。");
                }
                return "工具 ID、版本和清单指纹与工程锁一致；文件内容完整性仍由构建或深度检查核验。";
            }, project.ToolsetId, project.ToolsetVersion);
            if (project.Espressif is not null)
            {
                await InspectSdkAsync(root, project, primary, checks, token);
            }
        }
        await InspectCacheAsync(root, project, primary, checks, token);
        if (project.Espressif is not null || project.Kind == ProjectKind.CubeMx)
        {
            var analysis = await AnalysisEnvironmentInspector.InspectAsync(root, project, primary, token);
            checks.AddRange(analysis.Issues.Select(issue => new HealthCheck(issue.Code, issue.Title, HealthState.Warning,
                issue.Detail + "\n实时分析暂停使用这份配置；实际构建仍由原生工具验证。", "build-errors",
                issue.NeedsToolRepair ? HealthAction.Tools : issue.NeedsCacheRepair ? HealthAction.ResetCache : HealthAction.Configure,
                issue.RawDiagnostic, project.ToolsetId, project.ToolsetVersion)));
            if (analysis.Issues.Count == 0)
            {
                checks.Add(new("HEALTH_ANALYSIS", "实时分析配置", HealthState.Information, analysis.Summary, "build",
                analysis.HasDatabase ? HealthAction.None : HealthAction.Configure));
            }
        }
        var ambient = ToolsetEnvironment.AmbientVariables.Where(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))).ToArray();
        if (ambient.Length > 0)
        {
            checks.Add(new("HEALTH_AMBIENT", "外部开发环境组件变量", HealthState.Information,
            "检测到变量名：" + string.Join(", ", ambient) + "。构建服务会隔离这些变量；不修改系统环境，也不在报告中导出变量值。", "tool-environment"));
        }
        checks.Add(new("HEALTH_SCOPE", "检查范围", HealthState.Information,
            "检查不编译固件、不执行用户 CMake、不连接设备。通过不代表固件、Python SDK 依赖或硬件运行已验收；实际构建仍执行完整校验。", "build"));
        return Report();

        ProjectHealthReport Report() => new(root, project.Name, target, DateTimeOffset.UtcNow, deep, native, checks.ToArray());
        async Task CheckAsync(string code, string title, string help, HealthAction action, Func<Task<string>> run, string? id = null, string? version = null)
        {
            progress?.Report(title + "…");
            try
            {
                checks.Add(new(code, title, HealthState.Passed, await run(), help, action, ToolsetId: id, ToolsetVersion: version));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { checks.Add(Failure(code, title, help, action, error, id, version)); }
        }
    }

    public Task<ConfigurationCachePlan> PreviewCacheRepairAsync(string directory, CancellationToken token = default)
        => builds.PreviewConfigurationResetAsync(directory, token);
    public Task<string> RepairCacheAsync(ConfigurationCachePlan plan, CancellationToken token = default)
    {
        if (debugActive?.Invoke() == true)
        {
            throw new StudioXException("HEALTH_DEBUG_ACTIVE", "请先结束调试会话再修复配置缓存。");
        }
        return builds.ResetConfigurationCacheAsync(plan, token);
    }
    public Task ExportAsync(ProjectHealthReport report, string path, CancellationToken token = default) => JsonStore.WriteAsync(path, report, token);

    private static HealthCheck Failure(string code, string title, string help, HealthAction action, Exception error, string? id = null, string? version = null)
        => new(error is StudioXException studio ? studio.Code : code, title, HealthState.Error, error.Message,
            help, action, error.ToString(), id, version);
    private static async Task<byte[]> ReadLimitedAsync(string path, long limit, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        if (stream.Length > limit)
        {
            throw new StudioXException("HEALTH_FILE_SIZE", "检查文件过大，未加载：" + path);
        }
        using var output = new MemoryStream();
        var buffer = new byte[131072];
        int count;
        while ((count = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > limit)
            {
                throw new StudioXException("HEALTH_FILE_SIZE", "检查期间文件变大，未继续加载：" + path);
            }
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
