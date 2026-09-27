namespace StudioX.Desktop;

using StudioX.Application.Mcp;

/// <summary>拥有当前工程的 MCP 会话；等待已开始的工具操作结束后才释放设备和外部目录授权。</summary>
internal sealed class AiMcpSessionCoordinator(
    Func<string, int, bool> isCurrentProject,
    Func<string, int, CancellationToken, Task<StudioXMcpSession>> createSession,
    Action<string> log) : IAsyncDisposable
{
    private StudioXMcpSession? session;
    private Task activeCall = Task.CompletedTask;
    private Task disposal = Task.CompletedTask;

    public async Task<StudioXMcpSession> GetAsync(string project, int generation, CancellationToken token)
    {
        await disposal;
        EnsureCurrentProject(project, generation, token);
        if (session is { } existing &&
            string.Equals(existing.Project, project, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        var created = await createSession(project, generation, token);
        if (!isCurrentProject(project, generation))
        {
            await created.DisposeAsync();
            throw new OperationCanceledException("AI 工程已经切换。", token);
        }

        session = created;
        return created;
    }

    public void TrackCall(Task call) => activeCall = call;

    public void QueueDisposal()
    {
        var retired = session;
        session = null;
        if (retired is null)
        {
            return;
        }

        // 新工程不能继承旧会话的目录授权，也不能抢占旧工具尚在使用的设备。
        disposal = DisposeAfterAsync(disposal, activeCall, retired);
    }

    public async ValueTask DisposeAsync()
    {
        QueueDisposal();
        await disposal;
    }

    private void EnsureCurrentProject(string project, int generation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!isCurrentProject(project, generation))
        {
            throw new OperationCanceledException("AI 工程已经切换。", token);
        }
    }

    private async Task DisposeAfterAsync(Task previous, Task call, StudioXMcpSession retired)
    {
        try
        {
            await previous;
        }
        catch (Exception error)
        {
            log("上一 AI MCP 会话清理失败：" + error);
        }

        try
        {
            await call;
        }
        catch (Exception error)
        {
            // 请求错误已由请求入口显示；失败的请求同样必须释放其设备会话。
            log("AI 请求结束时报告的异常，继续清理会话：" + error);
        }

        try
        {
            await retired.DisposeAsync();
        }
        catch (Exception error)
        {
            log("AI MCP 会话清理失败：" + error);
        }
    }
}
