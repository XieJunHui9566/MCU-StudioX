namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application.Output;
using StudioX.Engine;

public partial class MainWindow
{
    /// <summary>独立配置、映射、仿真和验证副本也使用同一输出生命周期。</summary>
    private async Task<T> RunBuildOutputOperationAsync<T>(string phase,
        Func<IProgress<string>, IProgress<string>, Task<T>> operation,
        Func<T, bool?> outcome, Func<T, string> summary, CancellationToken token)
    {
        ShowBottom(0);
        BuildLog.Clear();
        BeginBuildOutput();
        ReportBuildActivity(phase);
        var lines = new OutputLineBuffer();
        void ToolLine(string text)
        {
            Log(text);
            ReportBuildToolLine(text);
        }
        async Task DrainAsync()
        {
            // 含最后一行的进程回调先于最终结论呈现，异常退出也保留不带换行的尾部。
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (lines.Flush() is { } tail)
            {
                ToolLine(tail);
            }
        }
        try
        {
            var report = await operation(
                new Progress<string>(text => { Status.Text = text; ReportBuildActivity(text); }),
                new Progress<string>(text => { foreach (var line in lines.Append(text)) { ToolLine(line); } }));
            await DrainAsync();
            var message = summary(report);
            Log(message);
            FinishBuildOutput(message, outcome(report));
            return report;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await DrainAsync();
            Log(phase + "已取消");
            FinishBuildOutput(phase + "已取消", null);
            throw;
        }
        catch (Exception)
        {
            await DrainAsync();
            FinishBuildOutput(phase + "失败，原始诊断见下方输出", false);
            // 外层 RunAsync 保留完整异常；此处只负责结束显示状态。
            throw;
        }
    }

    private Task<BuildReport> ConfigureWithOutputAsync(string directory, CancellationToken token) =>
        RunBuildOutputOperationAsync("CMake 配置", (progress, output) => services.Builds.ConfigureAsync(directory, progress, token, output),
            report => report.Success, report => $"CMake 配置{(report.Success ? "成功" : "失败")}，退出代码：{report.ExitCode?.ToString() ?? "不可用"}", token);
}
