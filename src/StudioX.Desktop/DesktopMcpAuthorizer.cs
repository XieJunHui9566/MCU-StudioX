namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application.Mcp;

/// <summary>副作用 MCP 调用由当前工程的 AI 聊天区逐次授权；窗口失效时默认拒绝。</summary>
internal sealed class DesktopMcpAuthorizer(
    Window owner,
    Func<string, bool> isCurrentProject,
    Func<StudioXMcpApprovalRequest, CancellationToken, Task<bool>> requestApproval)
    : IStudioXMcpAuthorizer
{
    public async Task<bool> ApproveAsync(StudioXMcpApprovalRequest request, CancellationToken token)
    {
        if (token.IsCancellationRequested || owner.Dispatcher.HasShutdownStarted) return false;
        try
        {
            // 工程身份和窗口状态只能在桌面线程读取；等待卡片选择时不阻塞消息循环。
            return await owner.Dispatcher.InvokeAsync(async () =>
            {
                if (token.IsCancellationRequested || !owner.IsLoaded || !isCurrentProject(request.Project))
                    return false;
                var approved = await requestApproval(request, token);
                return approved && !token.IsCancellationRequested && owner.IsLoaded &&
                    isCurrentProject(request.Project);
            }, DispatcherPriority.Normal).Task.Unwrap().ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (InvalidOperationException) when (owner.Dispatcher.HasShutdownStarted)
        {
            return false;
        }
    }
}
