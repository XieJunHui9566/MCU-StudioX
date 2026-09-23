namespace StudioX.Desktop;

public partial class MainWindow
{
    private async Task RefreshBuildMemoryAsync(string directory, CancellationToken token)
    {
        try
        {
            var report = await services.BuildMemory.ReadAsync(directory, token);
            if (!closing && projectDirectory == directory) BuildMemory.SetReport(report);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 分析是可选展示，不把统计异常转换为固件编译失败。
            Log("构建占用分析：" + ex);
            if (projectDirectory == directory) BuildMemory.SetMessage("暂无法分析，请查看构建日志。");
        }
    }
}
