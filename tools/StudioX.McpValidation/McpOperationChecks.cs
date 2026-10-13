using System.Reflection;
using System.Text.Json;
using StudioX.Application.Mcp;
using StudioX.Foundation;

/// <summary>同会话状态、过期取消、超时与退出清理边界；不启动设备会话。</summary>
internal static class McpOperationChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var type = typeof(StudioXMcpTools).Assembly.GetType("StudioX.Application.Mcp.McpOperationTracker", true)!;
        var tracker = Activator.CreateInstance(type, nonPublic: true)!;
        object Call(object target, string name, params object?[] arguments) => target.GetType()
            .GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, arguments)!;
        string State()
        {
            using var status = JsonDocument.Parse((string)Call(tracker, "Status"));
            return status.RootElement.GetProperty("state").GetString()!;
        }
        var operation = Call(tracker, "Start", "project_build", TimeSpan.FromSeconds(3), CancellationToken.None, "fixture.log");
        var id = operation.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operation)!;
        var output = (IProgress<string>)operation.GetType().GetProperty("Output", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operation)!;
        output.Report(new string('x', 5000));
        using (var status = JsonDocument.Parse((string)Call(tracker, "Status")))
        {
            check(status.RootElement.GetProperty("recentOutput").GetString()!.Length == 4096 &&
                status.RootElement.GetProperty("outputTruncated").GetBoolean(), "MCP operation preserves bounded live output with observable truncation");
        }
        try
        {
            Call(tracker, "Cancel", "other-session-id");
            check(false, "invalid operation id accepted");
        }
        catch (TargetInvocationException error) when (error.InnerException is StudioXException { Code: "MCP_OPERATION_ID" }) { }
        check(State() == "running", "foreign or stale cancel identifiers do not stop an active operation");
        try
        {
            Call(tracker, "Start", "firmware_download", TimeSpan.FromSeconds(3), CancellationToken.None, null);
            check(false, "concurrent operation accepted");
        }
        catch (TargetInvocationException error) when (error.InnerException is StudioXException { Code: "MCP_OPERATION_BUSY" }) { }
        Call(tracker, "Cancel", id);
        check(State() == "stopping", "MCP cancellation reports stopping until its owner has finished cleanup");
        Call(operation, "Interrupted", new OperationCanceledException());
        ((IDisposable)operation).Dispose();
        check(State() == "canceled", "MCP operation preserves its canceled final state after scope disposal");
        var expired = Call(tracker, "Start", "project_build", TimeSpan.FromMilliseconds(30), CancellationToken.None, null);
        var token = (CancellationToken)expired.GetType().GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(expired)!;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            check(false, "timeout did not cancel");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        check(Call(expired, "Interrupted", new OperationCanceledException()) is StudioXException { Code: "MCP_OPERATION_TIMEOUT" },
            "MCP timeout has a recoverable error code distinct from user cancellation");
        ((IDisposable)expired).Dispose();
        check(State() == "timed_out", "MCP operation retains the timeout after scope disposal");
        var active = Call(tracker, "Start", "project_build", TimeSpan.FromSeconds(3), CancellationToken.None, null);
        var closing = ((IAsyncDisposable)tracker).DisposeAsync().AsTask();
        check(!closing.IsCompleted && State() == "stopping", "MCP session close requests cancellation and waits for owned work to exit");
        ((IDisposable)active).Dispose();
        await closing.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
