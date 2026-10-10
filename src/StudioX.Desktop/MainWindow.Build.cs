namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application.Output;
using StudioX.Engine;

public partial class MainWindow
{
    private async Task<BuildReport> BuildWithSummaryAsync(string directory, CancellationToken token)
    {
        BeginBuildOutput();
        var outputLines = new OutputLineBuffer();
        void ToolLine(string text)
        {
            Log(text);
            ReportBuildToolLine(text);
        }
        void FlushToolOutput()
        {
            if (outputLines.Flush() is { } tail)
            {
                ToolLine(tail);
            }
        }
        CancelBuildMemoryRefresh();
        ClearBuildDiagnostics();
        var revision = diagnosticRevision;
        try
        {
            StudioX.Application.Health.ProjectHealthReport health;
            var checking = true;
            try
            {
                health = await services.ProjectHealth.InspectAsync(directory, progress: new Progress<string>(text =>
                {
                    if (checking)
                    {
                        Status.Text = text;
                        ReportBuildActivity(text);
                    }
                }), token: token);
            }
            finally { checking = false; }
            Log("编译前健康检查：" + health.Summary);
            guideHealthPassed = health.Errors == 0;
            RefreshFirstProjectGuide();
            if (!health.CanBuild)
            {
                PresentBuildHealth(health);
                BuildMemory.SetMessage("工程检查未满足原生构建条件，请处理健康检查中的问题。");
                FinishBuildOutput("工程检查失败，未启动编译", false);
                return new BuildReport(false, health.ToText(), []);
            }
            await SaveHdlBuildSettingsAsync(token);
            BuildMemory.SetMessage("正在编译，完成后更新占用…");
            var buildClock = System.Diagnostics.Stopwatch.StartNew();
            var report = await services.Builds.BuildAsync(directory,
                new Progress<string>(text => { Status.Text = text; ReportBuildActivity(text); }), token,
                new Progress<string>(text => { foreach (var line in outputLines.Append(text)) { ToolLine(line); } }));
            buildClock.Stop();
            // 等待 Progress 已投递的输出，避免工具尾部文本排到中文结论之后。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            FlushToolOutput();
            ReportBuildActivity("整理构建结果");
            try
            {
                await PublishBuildDiagnosticsAsync(directory, report.Log, revision, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ClearBuildDiagnostics();
                Log("编辑器错误标记不可用，原始诊断请查看构建输出：" + ex);
            }
            if (report.Success)
            {
                ReportBuildActivity("分析构建产物");
                await RefreshBuildMemoryAsync(directory, token);
                try
                {
                    await services.BuildHistory.CaptureAsync(directory, buildClock.Elapsed.TotalSeconds, token);
                }
                catch (Exception error) when (error is not OperationCanceledException) { Log("构建历史保存失败，原始编译结果保持有效：" + error); }
                if (currentProjectManifest?.Espressif is not null &&
                    string.Equals(projectDirectory, directory, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await services.Intelligence.StartAsync(directory, token);
                        QueueLiveDiagnostics();
                        services.Intelligence.RecordAnalysisLog(services.Intelligence.StatusDescription);
                        QueueOutlineRefresh(clear: true);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        // 固件构建已经成功；保留独立的索引故障诊断，不把它改写成编译失败。
                        await RecordAnalysisFailureAsync("SDK 索引刷新失败，原始编译结果仍有效", error);
                    }
                }
            }
            else
            {
                BuildMemory.SetMessage("编译失败，暂无本次占用数据。");
                ShowTroubleshooting(report.Log);
            }
            FinishBuildOutput(report.Summary, report.Success);
            return report;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ClearBuildDiagnostics();
            BuildMemory.SetMessage("编译已取消，请重新编译以更新占用。");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            FlushToolOutput();
            Log("编译已取消，退出代码：不可用（用户停止）");
            FinishBuildOutput("编译已取消", null);
            throw;
        }
        catch (Exception ex)
        {
            ClearBuildDiagnostics();
            BuildMemory.SetMessage("编译失败，暂无本次占用数据。");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            FlushToolOutput();
            Log(ex.ToString());
            FinishBuildOutput("编译失败，原始诊断见下方输出", false);
            ShowTroubleshooting(FailureDiagnostic(ex));
            // 校验或启动异常没有工具退出码；保留完整诊断，让调用方最后统一输出失败结论。
            return new BuildReport(false, ex.ToString(), []);
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            FlushToolOutput();
            if (currentProjectManifest?.PinMapping is not null && currentProjectManifest.Logic is null && projectDirectory == directory)
            {
                try
                {
                    await RefreshAg32PinMappingStatusAsync(CancellationToken.None);
                }
                catch (Exception error) { Log("时序状态刷新失败，原始构建结果保留：" + error); }
            }
        }
    }
}
