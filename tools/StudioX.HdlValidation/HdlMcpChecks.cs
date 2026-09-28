using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;

/// <summary>通过实际 MCP 会话确认只读发现、构建授权及未保存文件保护。</summary>
internal static class HdlMcpChecks
{
    internal static async Task RunAsync(string runtime, string project, string output, Action<bool, string> check)
    {
        var authorizer = new Authorizer();
        var dirty = false;
        await using var services = new WorkbenchService(runtime, Path.Combine(output, "mcp-data"));
        await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project, authorizer,
            () => Task.FromResult(dirty), includePlugins: false));
        var tools = await session.ListToolsAsync();
        check(tools.Count(tool => tool.Name.StartsWith("ag32_logic_schematic", StringComparison.Ordinal)) == 2, "MCP 发现两项电路图工具");
        var settings = await session.CallToolAsync("ag32_logic_schematic_settings", "{}");
        check(settings.Contains("user_logic", StringComparison.Ordinal) && authorizer.Requests.Count == 0, "MCP 读取配置不触发写授权");
        var denied = await session.CallToolAsync("ag32_logic_schematic_generate", "{}");
        check(denied.Contains("MCP_APPROVAL_DENIED", StringComparison.Ordinal) && authorizer.Requests.Single().Permission == StudioXMcpPermission.Build,
            "MCP 综合需要 Build 授权");
        authorizer.Allow = true;
        dirty = true;
        var unsaved = await session.CallToolAsync("ag32_logic_schematic_generate", "{}");
        check(unsaved.Contains("MCP_UNSAVED", StringComparison.Ordinal), "MCP 未保存源码不能隐式综合旧文件");
        dirty = false;
        using var response = JsonDocument.Parse(await session.CallToolAsync("ag32_logic_schematic_generate", "{}"));
        check(response.RootElement.GetProperty("TopModule").GetString() == "user_logic" &&
            File.Exists(response.RootElement.GetProperty("svg").GetString()) && !services.Debugger.IsActive,
            "MCP 成功返回实际网表和 SVG，未建立硬件会话");
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
