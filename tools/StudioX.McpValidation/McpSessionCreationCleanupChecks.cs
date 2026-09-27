using System.Reflection;
using StudioX.Application;
using StudioX.Application.Mcp;

/// <summary>模拟初始化失败时的关闭故障，确认后续所有者仍释放且原始异常没有丢失。</summary>
internal static class McpSessionCreationCleanupChecks
{
    public static async Task RunAsync(WorkbenchService services, string project, Action<bool, string> check)
    {
        var cleanup = typeof(StudioXMcpSession).GetMethod("CleanupFailedCreationAsync",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Task<AggregateException?> Cleanup(Exception original, Func<ValueTask> server, Func<ValueTask> tools) =>
            (Task<AggregateException?>)cleanup.Invoke(null, [original, server, tools])!;

        var originalError = new IOException("original MCP initialization diagnostic");
        var serverError = new IOException("server close diagnostic");
        var toolsError = new IOException("tools close diagnostic");
        var toolsReleased = false;
        var firstFailure = await Cleanup(originalError,
            () => ValueTask.FromException(serverError),
            () =>
            {
                toolsReleased = true;
                return ValueTask.CompletedTask;
            });
        check(toolsReleased && firstFailure?.InnerExceptions.Count == 2 &&
              ReferenceEquals(firstFailure.InnerExceptions[0], originalError) &&
              ReferenceEquals(firstFailure.InnerExceptions[1], serverError),
            "failed MCP server cleanup still releases tools and retains the original initialization error");

        var bothFailures = await Cleanup(originalError,
            () => ValueTask.FromException(serverError), () => ValueTask.FromException(toolsError));
        check(bothFailures?.InnerExceptions.Count == 3 &&
              ReferenceEquals(bothFailures.InnerExceptions[0], originalError) &&
              ReferenceEquals(bothFailures.InnerExceptions[1], serverError) &&
              ReferenceEquals(bothFailures.InnerExceptions[2], toolsError),
            "MCP initialization and both cleanup diagnostics are returned without replacement");

        var cleanResult = await Cleanup(originalError,
            () => ValueTask.CompletedTask, () => ValueTask.CompletedTask);
        check(cleanResult is null,
            "successful MCP creation cleanup leaves the original failure for an unchanged rethrow");

        await using var tools = new StudioXMcpTools(services, project, new DenyStudioXMcpAuthorizer());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = false;
        try
        {
            await using var session = await StudioXMcpSession.CreateAsync(tools, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        var resourcesReleased = false;
        try
        {
            _ = tools.CreateToolCollection();
        }
        catch (ObjectDisposedException)
        {
            resourcesReleased = true;
        }
        check(canceled && resourcesReleased,
            "canceled real MCP initialization closes its tools and preserves cancellation semantics");
    }
}
