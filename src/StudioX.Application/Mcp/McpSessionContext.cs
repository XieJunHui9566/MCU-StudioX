namespace StudioX.Application.Mcp;

using StudioX.Foundation;

/// <summary>各工具组共享的工程身份与授权入口；业务状态由所属工具组自行持有。</summary>
internal sealed class McpSessionContext
{
    private readonly IStudioXMcpAuthorizer authorizer;
    private readonly Func<Task<bool>>? hasUnsavedDocuments;

    internal McpSessionContext(WorkbenchService services, string project, IStudioXMcpAuthorizer authorizer,
        Func<Task<bool>>? hasUnsavedDocuments, WebResearchService? webResearch)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        if (string.IsNullOrWhiteSpace(project) || !Path.IsPathFullyQualified(project))
        {
            throw new StudioXException("MCP_PROJECT", "MCP 需要绑定一个绝对路径的工程目录。");
        }
        Project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        this.authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        this.hasUnsavedDocuments = hasUnsavedDocuments;
        WebResearch = webResearch ?? services.WebResearch;
        Workspace = new McpWorkspaceAccess(Project);
        Debug = new McpDebugAccess(services.Debugger, Project);
        ExternalProjects = new ExternalProjectAccess(this);
    }

    internal WorkbenchService Services
    {
        get;
    }
    internal string Project
    {
        get;
    }
    internal WebResearchService WebResearch
    {
        get;
    }
    internal McpWorkspaceAccess Workspace
    {
        get;
    }
    internal McpDebugAccess Debug
    {
        get;
    }
    internal ExternalProjectAccess ExternalProjects
    {
        get;
    }

    internal async Task RequireApprovalAsync(string tool, string summary,
        StudioXMcpPermission permission, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var approved = await authorizer.ApproveAsync(new(Project, tool, summary, permission), token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!approved)
        {
            throw new ModelContextProtocol.McpException("MCP_APPROVAL_DENIED: 用户没有授权本次 MCP 操作。");
        }
    }

    internal async Task RequireSavedDocumentsAsync()
    {
        if (hasUnsavedDocuments is not null && await hasUnsavedDocuments().ConfigureAwait(false))
        {
            throw new ModelContextProtocol.McpException("MCP_UNSAVED_FILES: 编辑器有未保存的改动，请先保存，再调用 MCP 工具。");
        }
    }
}
