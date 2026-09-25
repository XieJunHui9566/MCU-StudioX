using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;

internal static class DebugMcpAssistChecks
{
    public static async Task RunAsync(WorkbenchService services, StudioXMcpSession session,
        string project, Action<bool, string> check)
    {
        // 仅绑定隔离测试工程；这里不启动 GDB/OpenOCD，也不访问设备。
        await services.Debugger.OpenProjectAsync(project);
        var immediate = await session.CallToolAsync("debug_wait",
            "{\"expectedState\":\"Disconnected\",\"timeoutMs\":100}");
        using (var response = JsonDocument.Parse(immediate))
            check(response.RootElement.GetProperty("timedOut").GetBoolean() == false &&
                response.RootElement.GetProperty("state").GetString() == "Disconnected",
                "debug_wait returns an already reached offline state without polling");

        var timeout = await session.CallToolAsync("debug_wait",
            "{\"expectedState\":\"Stopped\",\"timeoutMs\":30}");
        using (var response = JsonDocument.Parse(timeout))
            check(response.RootElement.GetProperty("timedOut").GetBoolean() &&
                response.RootElement.GetProperty("state").GetString() == "Disconnected",
                "debug_wait reports bounded timeout without starting hardware");

        var noLog = await session.CallToolAsync("debug_log", "{}");
        check(noLog.Contains("DEBUG_LOG", StringComparison.Ordinal),
            "debug_log explains when no hardware session log exists");
    }
}
