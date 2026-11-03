namespace StudioX.Desktop;

using System.Windows.Media;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Output;
using StudioX.Engine;

public partial class MainWindow
{
    private async Task CheckMon51DownloadOutputAsync(string directory, List<string> checks, Func<Task> layout, Action<string> capture)
    {
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        async Task Drain()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            await layout();
        }
        bool HasColor(OutputTone tone)
        {
            var color = ((SolidColorBrush)BuildLog.BrushFor(tone)).Color;
            return BuildLog.TextArea.TextView.VisualLines.SelectMany(line => line.Elements)
                .Any(element => element.TextRunProperties.ForegroundBrush is SolidColorBrush brush && brush.Color == color);
        }
        var prepared = new StcIspPreparation(directory, "IAP15L2K61S2", 62464, "COM15", "monitor.bin", "snapshot.bin",
            "fixture", 61440, 61439, "unused", "unused", Path.Combine(directory, "fixture-monitor.log"), new(), MonitorSetup: true);
        var report = new StcIspReport(true, "STUDIOX_PROGRAM_COMPLETE\nSTUDIOX_MONITOR_SETUP_COMPLETE POWER_CYCLE_REQUIRED", prepared.LogPath, 0, false, true);
        var originalSize = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        var custom = ThemeService.Dark with
        {
            Id = "stc-output-custom",
            Colors = new(ThemeService.Dark.Colors)
            {
                ["Accent"] = "#B8E986",
                ["OnAccent"] = "#163020"
            }
        };
        IProgress<string>? lateOutput = null;
        try
        {
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
            {
                ApplyTheme(theme);
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = theme == custom ? 18d : 13d;
                BuildLog.Clear();
                var result = await RunStcDownloadWithOutputAsync(prepared, async (output, _) =>
                {
                    lateOutput = output;
                    Check(buildOutputActive && buildOutputState == "下载中" && !BuildLog.Text.Contains('%'), theme.Id + " monitor uses the ordinary ISP waiting activity");
                    output.Report("Waiting for MCU, please cycle power: done\r\nSTUDIOX_TARGET_VERIFIED IAP15L2K61S2\nSTUDIOX_ISP_PRO");
                    output.Report("GRESS 64 61440\r");
                    output.Report("\nwarning: retained monitor warning\nSTUDIOX_ISP_PROGRESS 30720 61440\n");
                    await Drain();
                    Check(BuildLog.Text.Contains("50% (30,720/61,440)") && !BuildLog.Text.Contains("STUDIOX_ISP_PROGRESS") &&
                        BuildLog.Text.Split('\n').Count(line => line.Contains("写入代码 Flash")) == 1,
                        theme.Id + " monitor chunks refresh the same byte progress bar without native tqdm or telemetry text");
                    Check(HasColor(OutputTone.Warning) && HasColor(OutputTone.Information), theme.Id + " monitor warnings and live progress use shared theme colors");
                    capture("monitor-progress-" + theme.Id + ".png");
                    output.Report("STUDIOX_ISP_PROGRESS 61440 61440\n");
                    await Drain();
                    Check(buildOutputActive && !BuildLog.Text.Contains("[成功]"), theme.Id + " monitor transfer at 100 percent waits for the final report");
                    output.Report("Finishing write: done\nSetting options: done\nSTUDIOX_PROGRAM_COMPLETE\nSTUDIOX_MONITOR_SETUP_COMPLETE POWER_CYCLE_REQUIRED");
                    return report;
                }, CancellationToken.None);
                await Drain();
                Check(result.Success && !buildOutputActive && !buildOutputTimer.IsEnabled && buildOutputState == "成功" &&
                    BuildLog.Text.Contains("[成功] STC 仿真监控下载成功") && BuildLog.Text.Contains("未执行 Flash 读回校验") && BuildLog.Text.Contains("下载日志："),
                    theme.Id + " monitor completion shares the ordinary ISP result, elapsed time and log path");
                Check(HasColor(OutputTone.Success), theme.Id + " actual monitor success glyphs receive the shared success color");
                capture("monitor-success-" + theme.Id + ".png");
                var finished = BuildLog.Text;
                lateOutput!.Report("STUDIOX_ISP_PROGRESS 1 61440\n");
                await Drain();
                Check(BuildLog.Text == finished && !buildOutputTimer.IsEnabled, theme.Id + " late monitor output cannot restart a completed operation");
            }
        }
        finally
        {
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = originalSize;
            ApplyTheme(ThemeService.Dark);
        }
        BuildLog.Clear();
        var failed = await RunStcDownloadWithOutputAsync(prepared, (output, _) =>
        {
            output.Report("STUDIOX_ISP_PROGRESS 61440 61440\nSerial port error: incorrect monitor finish packet");
            return Task.FromResult(report with
            {
                Success = false,
                ExitCode = 1
            });
        }, CancellationToken.None);
        await Drain();
        Check(!failed.Success && buildOutputState == "失败" && !buildOutputTimer.IsEnabled && !BuildLog.Text.Contains("[成功]") &&
            BuildLog.Text.Contains("incorrect monitor finish packet") && HasColor(OutputTone.Error),
            "monitor finish failure after 100 percent preserves unterminated diagnostics and displays failure color");
        capture("monitor-failure.png");
        BuildLog.Clear();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await RunStcDownloadWithOutputAsync(prepared, (output, token) =>
            {
                lateOutput = output;
                output.Report("warning: interrupted monitor write");
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult(report);
            }, cancellation.Token);
            throw new InvalidOperationException("Cancelled monitor accepted.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        await Drain();
        var cancelled = BuildLog.Text;
        lateOutput!.Report("STUDIOX_ISP_PROGRESS 61440 61440\n");
        await Drain();
        Check(buildOutputState == "取消" && !buildOutputTimer.IsEnabled && BuildLog.Text == cancelled &&
            BuildLog.Text.Contains("STC 仿真监控下载已取消") && BuildLog.Text.Contains("interrupted monitor write") && HasColor(OutputTone.Warning),
            "cancelled monitor preserves warning tail, uses the ordinary cancellation style and ignores late output");
        Check(BuildOutputParser.Tone("STC 仿真监控下载成功。请重新上电") == OutputTone.Success &&
            BuildOutputParser.Tone("STC 仿真监控下载失败") == OutputTone.Error &&
            BuildOutputParser.Tone("STC 仿真监控下载已取消") == OutputTone.Warning,
            "monitor status text and final progress states share semantic colors with ordinary STC downloads");
        capture("monitor-cancelled.png");
    }
}
