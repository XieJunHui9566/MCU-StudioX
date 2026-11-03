namespace StudioX.PluginValidation;

using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Mcp;
using StudioX.Application.Plugins;
using StudioX.Foundation;

/// <summary>通过真实 MCP 审批拒绝验证结构化业务错误进入插件异常契约，不解析错误文案猜码。</summary>
internal static class HostErrorChecks
{
    public static async Task RunAsync(WorkbenchService services, string project, ValidationChecks checks)
    {
        var original = await services.Files.ReadAsync(project, "src/main.c");
        var arguments = JsonSerializer.SerializeToElement(new
        {
            path = "src/main.c",
            originalSha256 = original.DiskHash,
            content = "int denied_change_must_not_be_written;\n"
        });
        await using var session = await StudioXMcpSession.CreateAsync(new StudioXMcpTools(services, project,
            new DenyStudioXMcpAuthorizer(), includePlugins: false));
        var response = await session.CallToolDetailedAsync("project_edit_file", arguments.GetRawText());
        using var error = JsonDocument.Parse(response.Text);
        var code = error.RootElement.GetProperty("code").GetString();
        var message = error.RootElement.GetProperty("message").GetString();
        checks.Check(code == "MCP_APPROVAL_DENIED" && !string.IsNullOrWhiteSpace(message) &&
            error.RootElement.TryGetProperty("error", out _), "real MCP denial produces the structured error/code/message contract");

        await using var broker = new PluginWorkspaceBroker(services, project, new DenyStudioXMcpAuthorizer());
        var completed = 0;
        broker.OperationCompleted += (_, _) => Interlocked.Increment(ref completed);
        try
        {
            _ = await broker.CallAsync("validation.structured-errors", "project_edit_file", arguments);
            throw new InvalidOperationException("插件桥没有把真实 MCP 审批拒绝映射为异常。");
        }
        catch (StudioXException exception) when (exception.Code == code && exception.Message == message)
        {
            checks.Check(true, "plugin broker preserves the exact structured MCP denial Code and message");
        }
        var after = await services.Files.ReadAsync(project, "src/main.c");
        checks.Check(after.DiskHash == original.DiskHash && Volatile.Read(ref completed) == 0,
            "rejected MCP host operation leaves source unchanged and publishes no successful operation event");
        await checks.RejectAsync(async () =>
        {
            _ = await broker.CallAsync("validation.structured-errors", "validation_missing_tool", JsonSerializer.SerializeToElement(new
            {
            }));
        }, "unavailable host tool keeps its stable plugin error Code", "PLUGIN_HOST_TOOL");
    }
}
