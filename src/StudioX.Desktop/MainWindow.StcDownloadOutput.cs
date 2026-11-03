namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application.Output;
using StudioX.Engine;

public partial class MainWindow
{
    private void PresentStcToolLine(string line)
    {
        var activity = StcIspOutputParser.Activity(line);
        if (activity is not null)
        {
            ReportBuildActivity(activity.Phase, activity.Measurement);
            if (activity.Measurement is not null)
            {
                // 字节进度只替换活动行；逐块原始记录仍保存在 stcgal.log。
                return;
            }
        }
        if (line.Length > 0)
        {
            Log(line);
        }
    }

    private async Task DownloadStcWithOutputAsync(StcIspPreparation prepared, CancellationToken token)
    {
        try
        {
            await RunStcDownloadWithOutputAsync(prepared,
                (output, cancel) => services.StcIsp.DownloadPreparedAsync(prepared, output, cancel), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>普通用户固件与仿真监控共用分块解析、活动行、诊断配色和最终状态。</summary>
    private async Task<StcIspReport> RunStcDownloadWithOutputAsync(StcIspPreparation prepared,
        Func<IProgress<string>, CancellationToken, Task<StcIspReport>> operation, CancellationToken token)
    {
        BeginBuildOutput(prepared.Settings.DownloadMode == StcDownloadMode.NativeUsb
            ? "等待原生 USB ISP；请使目标板进入下载模式后插入电脑" : "等待 MCU 上电；请按开发板电源/下载键", "下载中");
        var lines = new OutputLineBuffer();
        var acceptingOutput = true;
        async Task DrainAsync()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (lines.Flush() is { } tail)
            {
                PresentStcToolLine(tail);
            }
        }
        try
        {
            var result = await operation(new Progress<string>(chunk =>
                {
                    if (!acceptingOutput)
                    {
                        return;
                    }
                    foreach (var line in lines.Append(chunk))
                    {
                        PresentStcToolLine(line);
                    }
                }), token);
            await DrainAsync();
            PresentStcDownloadResult(prepared, result);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await DrainAsync();
            StcIspStatus.Text = Status.Text = (prepared.MonitorSetup ? "STC 仿真监控下载已取消" : "STC 下载已取消") + "；若已开始擦写，板内程序可能不完整。";
            FinishBuildOutput(Status.Text, null);
            throw;
        }
        catch (Exception)
        {
            await DrainAsync();
            FinishBuildOutput((prepared.MonitorSetup ? "STC 仿真监控下载失败" : "STC 下载失败") + "，原始诊断见输出", false);
            throw;
        }
        finally { acceptingOutput = false; }
    }
}
