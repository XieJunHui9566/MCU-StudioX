namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Engine;

public partial class MainWindow
{
    private async Task<BuildReport> BuildWithSummaryAsync(string directory, CancellationToken token)
    {
        ClearBuildDiagnostics();
        var revision = diagnosticRevision;
        try
        {
            BuildMemory.SetMessage("正在编译，完成后更新占用…");
            var report = await services.Builds.BuildAsync(directory,
                new Progress<string>(text => { Status.Text = text; Log(text); }), token,
                new Progress<string>(text => Log(text.TrimEnd('\r', '\n'))));
            // 等待 Progress 已投递的输出，避免工具尾部文本排到中文结论之后。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            try { await PublishBuildDiagnosticsAsync(directory, report.Log, revision, token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { ClearBuildDiagnostics(); Log("编辑器错误标记不可用，原始诊断请查看构建输出：" + ex); }
            if (report.Success) await RefreshBuildMemoryAsync(directory, token);
            else BuildMemory.SetMessage("编译失败，暂无本次占用数据。");
            return report;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ClearBuildDiagnostics();
            BuildMemory.SetMessage("编译已取消，请重新编译以更新占用。");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Log("编译已取消，退出代码：不可用（用户停止）");
            throw;
        }
        catch (Exception ex)
        {
            ClearBuildDiagnostics();
            BuildMemory.SetMessage("编译失败，暂无本次占用数据。");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            Log(ex.ToString());
            // 校验或启动异常没有工具退出码；保留完整诊断，让调用方最后统一输出失败结论。
            return new BuildReport(false, ex.ToString(), []);
        }
    }
}
