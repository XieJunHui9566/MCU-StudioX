namespace StudioX.Application.Mcp;

using System.Reflection;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

/// <summary>组合独立工具组，为内置 Agent 与外部 stdio 客户端提供同一协议和会话生命周期。</summary>
public sealed class StudioXMcpTools : IAsyncDisposable
{
    private readonly McpSessionContext context;
    private readonly IReadOnlyList<StudioXMcpToolProvider> providers;
    private int disposed;

    public StudioXMcpTools(WorkbenchService services, string project, IStudioXMcpAuthorizer authorizer,
        Func<Task<bool>>? hasUnsavedDocuments = null, WebResearchService? webResearch = null)
    {
        context = new McpSessionContext(services, project, authorizer, hasUnsavedDocuments, webResearch);
        providers =
        [
            new WorkspaceMcpTools(context),
            new BuildMcpTools(context),
            new GitMcpTools(context),
            new ExternalProjectMcpTools(context),
            new ExternalProjectCopyMcpTools(context),
            new DebugMcpTools(context),
            new SerialPlotMcpTools(context),
            new FirmwareDownloadMcpTools(context),
            new StcIspMcpTools(context),
            new DeviceCatalogMcpTools(context),
            new LvglMcpTools(context),
            new SkillMcpTools(context),
            new PdfMcpTools(context),
            new QmdMcpTools(context),
            new MicrochipMcpTools(context),
            new WebMcpTools(context)
        ];
    }

    public WorkbenchService Services => context.Services;
    public string Project => context.Project;

    public IReadOnlyList<McpServerTool> CreateToolCollection()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var tools = providers.SelectMany(provider => provider.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(method => WrapToolErrors(provider, method)))
            .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
            .ToArray();
        // 名称重复会使两个客户端的发现结果不确定，必须在启动会话前拒绝。
        var duplicate = tools.GroupBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"重复的 MCP 工具名称：{duplicate.Key}。");
        }
        return tools;
    }

    private static McpServerTool WrapToolErrors(StudioXMcpToolProvider provider, MethodInfo method)
    {
        var original = McpServerTool.Create(method, provider);
        var function = AIFunctionFactory.Create(method, provider, new AIFunctionFactoryOptions
        {
            Name = original.ProtocolTool.Name,
            Description = original.ProtocolTool.Description,
            MarshalResult = (result, _, _) => ValueTask.FromResult(result)
        });
        // SDK 会泛化普通异常；仅业务错误通过脱敏适配器向模型传递可修复的诊断。
        return McpServerTool.Create(new StudioXMcpDiagnosticFunction(function),
            new McpServerToolCreateOptions { Metadata = original.Metadata });
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        List<Exception> errors = [];
        foreach (var provider in providers.OfType<IAsyncDisposable>())
        {
            try
            {
                await provider.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 一个设备关闭失败时仍清理其他所有者，最后把完整诊断交给宿主。
                errors.Add(error);
            }
        }
        context.ExternalProjects.ClearGrants();
        if (errors.Count > 0)
        {
            throw new AggregateException("MCP 工具组清理失败。", errors);
        }
    }
}
