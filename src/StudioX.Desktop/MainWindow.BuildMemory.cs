namespace StudioX.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? buildMemoryCancellation;
    private Task buildMemoryRefreshTask = Task.CompletedTask;
    private int buildMemoryRevision;
    private void CancelBuildMemoryRefresh()
    {
        buildMemoryRevision++;
        buildMemoryCancellation?.Cancel();
        buildMemoryCancellation = null;
    }
    private void QueueBuildMemoryRefresh(string directory)
    {
        CancelBuildMemoryRefresh();
        var cancellation = new CancellationTokenSource();
        buildMemoryCancellation = cancellation;
        buildMemoryRefreshTask = RefreshBuildMemoryInBackgroundAsync(directory, buildMemoryRevision, cancellation);
    }
    private async Task RefreshBuildMemoryInBackgroundAsync(string directory, int revision, CancellationTokenSource cancellation)
    {
        bool IsCurrent() => !closing && !cancellation.IsCancellationRequested && buildMemoryRevision == revision && projectDirectory == directory;
        var progress = new Progress<string>(message =>
        {
            if (IsCurrent())
            {
                BuildMemory.SetMessage("后台更新内存统计 · " + message);
            }
        });
        try
        {
            // 内存统计是可选信息，工具完整校验在后台继续；不占住工程打开和编辑操作。
            var report = await services.BuildMemory.ReadAsync(directory, cancellation.Token, progress);
            if (IsCurrent())
            {
                BuildMemory.SetReport(report);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!closing && revision == buildMemoryRevision && projectDirectory == directory)
            {
                BuildMemory.SetMessage("已取消后台内存统计；编译后可重新分析。");
            }
        }
        catch (Exception ex)
        {
            Log("后台构建占用分析：" + ex);
            if (IsCurrent())
            {
                BuildMemory.SetMessage("暂无法分析，请查看构建日志。");
            }
        }
        finally
        {
            if (ReferenceEquals(buildMemoryCancellation, cancellation))
            {
                buildMemoryCancellation = null;
                UpdateProjectActions(projectActionsBusy);
            }
            cancellation.Dispose();
        }
    }
    private async Task RefreshBuildMemoryAsync(string directory, CancellationToken token)
    {
        CancelBuildMemoryRefresh();
        var revision = buildMemoryRevision;
        try
        {
            var report = await services.BuildMemory.ReadAsync(directory, token);
            if (!closing && projectDirectory == directory && revision == buildMemoryRevision)
            {
                BuildMemory.SetReport(report);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 分析是可选展示，不把统计异常转换为固件编译失败。
            Log("构建占用分析：" + ex);
            if (projectDirectory == directory && revision == buildMemoryRevision)
            {
                BuildMemory.SetMessage("暂无法分析，请查看构建日志。");
            }
        }
    }
}
