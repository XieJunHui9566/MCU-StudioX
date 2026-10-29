namespace StudioX.Engine;

public sealed partial class BuildService
{
    private IReadOnlyList<IProjectBuildBackend>? backends;
    // 仅在构建互斥内创建注册表；原生流水线与框架流水线保留各自的内容锁和凭据语义。
    private IReadOnlyList<IProjectBuildBackend> Backends => backends ??=
    [
        new DelegatingProjectBuildBackend(ProjectKind.Pack, NativeAsync),
        new DelegatingProjectBuildBackend(ProjectKind.CubeMx, NativeAsync),
        new DelegatingProjectBuildBackend(ProjectKind.Arduino, request => Arduino.BuildAsync(request.Root, request.Output, request.Token)),
        new DelegatingProjectBuildBackend(ProjectKind.Pic, async request =>
        {
            StudioX.Engine.Pic.PicBuildService.Invalidate(request.Root);
            using var components = await PrepareComponentsAsync(request.Root, request.Project, request.Progress, request.Token);
            return await Pic.ExecuteAsync(request.Root, request.Project,
                components.Tools.Single(tool => tool.Manifest.Id == request.Project.ToolsetId), request.Output, request.Token);
        }),
        new DelegatingProjectBuildBackend(ProjectKind.Zephyr, async request =>
        {
            Zephyr.ZephyrBuildService.Invalidate(request.Root);
            using var components = await PrepareComponentsAsync(request.Root, request.Project, request.Progress, request.Token);
            return await new Zephyr.ZephyrBuildService(userDataDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCUStudioX"))
                .ExecuteAsync(request.Root, request.Project, components.Tools.Single(tool => tool.Manifest.Id == request.Project.ToolsetId),
                    request.ConfigureOnly, request.Output, request.Token);
        })
    ];

    private Task<BuildReport> NativeAsync(BuildBackendRequest request) => request.Project.ToolsetId == Mx251Profile.ToolsetId
        ? ExecuteMx251Async(request)
        : ExecuteNativeAsync(request.Root, request.Project, request.ConfigureOnly, request.Progress, request.Token, request.Output);

    private async Task<BuildReport> ExecuteMx251Async(BuildBackendRequest request)
    {
        using var operation = Mx251BuildStorage.Acquire(request.Root);
        using var outputLease = Mx251SdkProfile.Matches(request.Project) ? Mx251OutputLease.Acquire(request.Root) : null;
        Mx251BuildService.Invalidate(request.Root);
        using var components = await PrepareComponentsAsync(request.Root, request.Project, request.Progress, request.Token);
        return await Mx251BuildService.ExecuteAsync(request.Root, request.Project,
            components.Tools.Single(tool => tool.Manifest.Id == request.Project.ToolsetId), request.Output, request.Token,
            components.Tools.SingleOrDefault(tool => tool.Manifest.Id == Mx251CMake.ToolsetId), request.ConfigureOnly, outputLease?.Token);
    }
}
