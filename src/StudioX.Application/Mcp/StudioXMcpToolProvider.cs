namespace StudioX.Application.Mcp;

using StudioX.Engine;

/// <summary>工具组的最小公共入口；公共校验委托给会话服务，不共享业务字段。</summary>
internal abstract class StudioXMcpToolProvider(McpSessionContext context)
{
    protected McpSessionContext Context { get; } = context;
    protected WorkbenchService Services => Context.Services;
    protected string Project => Context.Project;
    protected WebResearchService WebResearch => Context.WebResearch;

    protected Task RequireApprovalAsync(string tool, string summary,
        StudioXMcpPermission permission, CancellationToken token) =>
        Context.RequireApprovalAsync(tool, summary, permission, token);

    protected Task RequireSavedDocumentsAsync() => Context.RequireSavedDocumentsAsync();

    protected Task<ProjectManifest> RequireWorkspaceProjectAsync(CancellationToken token) =>
        Context.Workspace.RequireWorkspaceProjectAsync(token);

    protected string RequireWorkspaceSourceFile(string relative) =>
        Context.Workspace.RequireWorkspaceSourceFile(relative);

    protected string RequireWorkspaceWritableSourceFile(string relative) =>
        Context.Workspace.RequireWorkspaceWritableSourceFile(relative);

    protected void RequireWorkspaceSourceDirectory(string relative) =>
        Context.Workspace.RequireWorkspaceSourceDirectory(relative);

    protected void RequireWorkspaceWritableSourceDirectory(string relative) =>
        Context.Workspace.RequireWorkspaceWritableSourceDirectory(relative);

    protected Task<string> RequireExternalRootAsync(string rootId, CancellationToken token) =>
        Context.ExternalProjects.RequireExternalRootAsync(rootId, token);

    protected static string LimitOutput(string value, int maximum = 16_000) =>
        value.Length <= maximum ? value : value[..maximum] + "\n[输出已截断]";
}
