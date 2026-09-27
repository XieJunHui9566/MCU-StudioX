namespace StudioX.Desktop;

/// <summary>工程切换的退出顺序；所有旧工程请求结束后，调用方才能绑定新工程与文档。</summary>
internal sealed class ProjectTransitionCoordinator(
    Func<Task> stopAgentAsync,
    Func<Task> persistBreakpointsAsync,
    Func<CancellationToken, Task> stopPreviewAsync,
    Func<CancellationToken, Task> stopDebuggerAsync,
    Func<CancellationToken, Task> stopEditorAsync)
{
    public async Task StopCurrentAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await stopAgentAsync();
        token.ThrowIfCancellationRequested();
        await persistBreakpointsAsync();
        await stopPreviewAsync(token);
        // 断点先持久化；调试导航结束后才能释放编辑器文本锚点。
        await stopDebuggerAsync(token);
        await stopEditorAsync(token);
        token.ThrowIfCancellationRequested();
    }
}
