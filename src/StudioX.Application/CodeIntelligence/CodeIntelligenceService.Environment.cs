namespace StudioX.Application.CodeIntelligence;

using StudioX.Engine;

public sealed partial class CodeIntelligenceService
{
    private string analysisDirectory = "";
    private string? analysisInputs;
    private ProjectManifest? analysisProject;
    private string[] analysisResponseFiles = [];
    private string? lastEnvironmentFailure;
    private DateTimeOffset unexpectedRestartWindow;
    private int unexpectedRestartCount;
    private bool retryDisconnectedServer;

    /// <summary>外部配置变化后重建语言会话；保留调用方的编辑缓冲区，不执行固件配置或编译。</summary>
    public Task<bool> RefreshEnvironmentAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await RefreshEnvironmentCoreAsync(token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }, token);

    private async Task<bool> RefreshEnvironmentCoreAsync(CancellationToken token)
    {
        if (analysisDirectory.Length == 0 || DiagnosticsSuspended)
        {
            return false;
        }
        ProjectManifest? project = analysisProject;
        string? current = null;
        try
        {
            project = await ProjectService.ReadAsync(analysisDirectory, token).ConfigureAwait(false);
            current = AnalysisInputStamp.Capture(runtimeDirectory, analysisDirectory, project, analysisResponseFiles);
            var unchanged = current == analysisInputs;
            if (unchanged && IsReady)
            {
                return false;
            }
            if (unchanged && connection is null && !retryDisconnectedServer)
            {
                return false;
            }
            if (unchanged)
            {
                var now = DateTimeOffset.UtcNow;
                if (now >= unexpectedRestartWindow)
                {
                    unexpectedRestartWindow = now.AddMinutes(1);
                    unexpectedRestartCount = 0;
                }
                if (unexpectedRestartCount >= 3)
                {
                    StatusDescription = "实时分析连续退出，暂停自动重启一分钟；可在工程健康检查中重新配置。";
                    return false;
                }
                unexpectedRestartCount++;
                retryDisconnectedServer = true;
                log.Enqueue("语言服务异常退出，撤销旧诊断并尝试重启（本分钟 " + unexpectedRestartCount + "/3）：" + connection?.Failure);
            }
            else
            {
                unexpectedRestartWindow = DateTimeOffset.MinValue;
                unexpectedRestartCount = 0;
                retryDisconnectedServer = false;
            }
            // 在同一排他锁内重载，旧进程的延迟诊断由 generation 隔离，不能重新覆盖新结果。
            await StartCoreAsync(analysisDirectory, token).ConfigureAwait(false);
            lastEnvironmentFailure = null;
            retryDisconnectedServer = false;
            log.Enqueue((unchanged ? "语言服务已恢复。" : "工程分析配置已变化，语言服务已重新加载。") + StatusDescription);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            ResetDiagnosticSession();
            var previous = connection;
            connection = null;
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            analysisInputs = current;
            analysisProject = project;
            StatusDescription = retryDisconnectedServer ? "实时分析重启失败，稍后重试；可打开工程健康检查。" : "实时分析等待配置修复；请打开工程健康检查。";
            if (lastEnvironmentFailure == error.ToString())
            {
                return false;
            }
            lastEnvironmentFailure = error.ToString();
            log.Enqueue("分析配置刷新失败，旧诊断已撤销：" + error);
            throw;
        }
    }
}
