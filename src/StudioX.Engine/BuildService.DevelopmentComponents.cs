namespace StudioX.Engine;

using StudioX.Foundation;

public sealed partial class BuildService
{
    private sealed class ComponentScope : IDisposable
    {
        public List<ToolUsageLease> Leases { get; } = [];
        public List<ResolvedToolset> Tools { get; } = [];
        public void Dispose()
        {
            foreach (var lease in Leases)
            {
                lease.Dispose();
            }
        }
    }

    private async Task<ComponentScope> PrepareComponentsAsync(string root, ProjectManifest project, IProgress<string>? progress, CancellationToken token)
    {
        var scope = new ComponentScope();
        try
        {
            var needs = await ProjectDevelopmentComponents.ReadAsync(root, project, token);
            var pins = await ProjectDevelopmentComponents.ReadPinsAsync(root, needs, token);
            foreach (var need in needs)
            {
                progress?.Report($"检查开发环境组件：{need.Id} / {need.Version}…");
                scope.Leases.Add(ToolUsageLease.Acquire(PathBoundary.Resolve(catalog.RootDirectory, need.Id + "/" + need.Version)));
                var tools = await catalog.ResolveAsync(need.Id, need.Version, need.CompilerId, token, progress: progress);
                ProjectDevelopmentComponents.CheckFingerprint(pins, need.Id, tools.Fingerprint);
                scope.Tools.Add(tools);
            }
            // 大型 SDK 校验可能持续较久，外部修改工程声明或锁后必须重新核对，不能发布旧配置的锁。
            var current = await ProjectService.ReadAsync(root, token);
            if (current != project || !needs.SequenceEqual(await ProjectDevelopmentComponents.ReadAsync(root, current, token)))
            {
                throw new StudioXException("TOOLS_PROJECT_CHANGED", "工具校验期间工程开发环境组件需求发生变化，请重新构建。");
            }
            pins = await ProjectDevelopmentComponents.ReadPinsAsync(root, needs, token);
            foreach (var tools in scope.Tools)
            {
                ProjectDevelopmentComponents.CheckFingerprint(pins, tools.Manifest.Id, tools.Fingerprint);
            }
            // 所有组件校验成功后才建立内容锁；缺失或内容不符时不写锁，也不运行生成器或编译器。
            var primary = scope.Tools.Single(t => t.Manifest.Id == project.ToolsetId);
            var primaryLock = PathBoundary.Resolve(root, ".studiox/toolchain.lock.json");
            var componentLock = PathBoundary.Resolve(root, DevelopmentComponentLock.RelativePath);
            token.ThrowIfCancellationRequested();
            if (!File.Exists(primaryLock))
            {
                await JsonStore.WriteAsync(primaryLock, new ToolchainLock(1, project.ToolsetId, project.ToolsetVersion, primary.Fingerprint), token);
            }
            if (!File.Exists(componentLock))
            {
                await JsonStore.WriteAsync(componentLock, new DevelopmentComponentLock(1, scope.Tools.Select(t =>
                new DevelopmentComponentPin(t.Manifest.Id, t.Manifest.Version, t.Manifest.Host, t.Manifest.CompilerId, t.Fingerprint)).ToArray()), token);
            }
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }
}
