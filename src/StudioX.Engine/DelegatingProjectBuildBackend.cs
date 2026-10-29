namespace StudioX.Engine;

internal sealed class DelegatingProjectBuildBackend(ProjectKind kind, Func<BuildBackendRequest, Task<BuildReport>> execute) : IProjectBuildBackend
{
    public ProjectKind Kind => kind;
    public Task<BuildReport> ExecuteAsync(BuildBackendRequest request) => execute(request);
}
