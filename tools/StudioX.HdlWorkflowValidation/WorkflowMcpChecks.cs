using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;

/// <summary>通过真实 MCP 会话校验配置、授权和仿真，测试过程不建立硬件会话。</summary>
internal static class WorkflowMcpChecks
{
    internal static async Task RunAsync(string runtime, string project, string output, Action<bool, string> check)
    {
        var authorizer = new Authorizer();
        var dirty = false;
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "mcp-data"));
        await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer,
            () => Task.FromResult(dirty), includePlugins: false));
        var tools = await session.ListToolsAsync();
        check(tools.Any(tool => tool.Name == "ag32_logic_simulate") && tools.Any(tool => tool.Name == "ag32_logic_workflow_settings"), "MCP 发现构建配置和仿真工具");
        var settings = await session.CallToolAsync("ag32_logic_workflow_settings", "{}");
        check(settings.Contains("tb_logic") && authorizer.Requests.Count == 0, "MCP 读取配置免写授权");
        var denied = await session.CallToolAsync("ag32_logic_simulate", "{}");
        check(denied.Contains("MCP_APPROVAL_DENIED") && authorizer.Requests.Single().Permission == StudioXMcpPermission.Build, "MCP 仿真需要 Build 授权");
        authorizer.Allow = true;
        dirty = true;
        check((await session.CallToolAsync("ag32_logic_simulate", "{}")).Contains("MCP_UNSAVED"), "MCP 拒绝用未保存源码仿真");
        dirty = false;
        using var response = JsonDocument.Parse(await session.CallToolAsync("ag32_logic_simulate", "{}"));
        check(response.RootElement.GetProperty("mode").GetString() == "RTL" &&
            File.Exists(response.RootElement.GetProperty("VcdPath").GetString()) && !services.Debugger.IsActive,
            "MCP 返回真实 VCD 且不连接硬件");
    }
    private sealed class Authorizer : IStudioXMcpAuthorizer
    {
        public bool Allow
        {
            get; set;
        }
        public List<StudioXMcpApprovalRequest> Requests { get; } = [];
        public Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return Task.FromResult(Allow);
        }
    }
}
