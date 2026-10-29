namespace StudioX.Engine;

internal interface IProjectBuildBackend
{
    ProjectKind Kind
    {
        get;
    }
    Task<BuildReport> ExecuteAsync(BuildBackendRequest request);
}
