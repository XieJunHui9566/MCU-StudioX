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
        BeginBuildOutput("等待 MCU 上电；请按开发板电源/下载键", "下载中");
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
            var result = await services.StcIsp.DownloadPreparedAsync(prepared,
                new Progress<string>(chunk =>
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
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await DrainAsync();
            StcIspStatus.Text = Status.Text = "STC 下载已取消；若已开始擦写，板内程序可能不完整。";
            FinishBuildOutput(Status.Text, null);
        }
        catch (Exception)
        {
            await DrainAsync();
            FinishBuildOutput("STC 下载失败，原始诊断见输出", false);
            throw;
        }
        finally { acceptingOutput = false; }
    }
}
