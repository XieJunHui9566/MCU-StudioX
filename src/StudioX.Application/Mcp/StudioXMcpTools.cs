namespace StudioX.Application.Mcp;

using System.Reflection;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using StudioX.Foundation;

/// <summary>工程操作绑定当前工程；外部示例只读与所有副作用均须由宿主授权。</summary>
public enum StudioXMcpPermission
{
    ExternalRead,
    FileWrite,
    Build,
    FirmwareDownload,
    GitWrite,
    GitRemote,
    DebugControl,
    HardwareConnect,
    SerialConnect,
    SerialSend,
    PlotConnect
}

public sealed record StudioXMcpApprovalRequest(string Project, string Tool, string Summary,
    StudioXMcpPermission Permission);

public interface IStudioXMcpAuthorizer
{
    Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token);
}

public sealed class DenyStudioXMcpAuthorizer : IStudioXMcpAuthorizer
{
    public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token) =>
        Task.FromResult(false);
}

/// <summary>桌面内置 Agent 与外部 stdio 服务共用的 MCP 工具实现。</summary>
public sealed partial class StudioXMcpTools
{
    private readonly IStudioXMcpAuthorizer authorizer;
    private readonly Func<Task<bool>>? hasUnsavedDocuments;
    private readonly WebResearchService webResearch;

    public StudioXMcpTools(WorkbenchService services, string project, IStudioXMcpAuthorizer authorizer,
        Func<Task<bool>>? hasUnsavedDocuments = null, WebResearchService? webResearch = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        if (string.IsNullOrWhiteSpace(project) || !Path.IsPathFullyQualified(project))
            throw new StudioXException("MCP_PROJECT", "MCP 需要绑定一个绝对路径的工程目录。");
        Project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        this.authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        this.hasUnsavedDocuments = hasUnsavedDocuments;
        this.webResearch = webResearch ?? services.WebResearch;
    }

    public WorkbenchService Services { get; }
    public string Project { get; }

    internal async Task RequireApprovalAsync(string tool, string summary,
        StudioXMcpPermission permission, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var approved = await authorizer.ApproveAsync(new(Project, tool, summary, permission), token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!approved)
            throw new ModelContextProtocol.McpException("MCP_APPROVAL_DENIED: 用户没有授权本次 MCP 操作。");
    }

    internal static string LimitOutput(string value, int maximum = 16_000) =>
        value.Length <= maximum ? value : value[..maximum] + "\n[输出已截断]";

    internal async Task RequireSavedDocumentsAsync()
    {
        if (hasUnsavedDocuments is not null && await hasUnsavedDocuments().ConfigureAwait(false))
            throw new ModelContextProtocol.McpException("MCP_UNSAVED_FILES: 编辑器有未保存的改动，请先保存，再调用 MCP 工具。");
    }

    public IReadOnlyList<McpServerTool> CreateToolCollection() =>
        GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(WrapToolErrors)
            .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
            .ToArray();

    private McpServerTool WrapToolErrors(MethodInfo method)
    {
        var original = McpServerTool.Create(method, this);
        var function = AIFunctionFactory.Create(method, this, new AIFunctionFactoryOptions
        {
            Name = original.ProtocolTool.Name,
            Description = original.ProtocolTool.Description,
            MarshalResult = (result, _, _) => ValueTask.FromResult(result)
        });
        // SDK 会把普通异常改写成泛化错误；只将业务异常转为可安全展示的 MCP 执行错误。
        return McpServerTool.Create(new StudioXMcpDiagnosticFunction(function),
            new McpServerToolCreateOptions { Metadata = original.Metadata });
    }
}
