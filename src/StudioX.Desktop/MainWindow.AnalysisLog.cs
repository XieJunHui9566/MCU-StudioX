namespace StudioX.Desktop;

using System.Windows;

public partial class MainWindow
{
    private bool analysisLogStorageFailureShown;
    private async Task RecordAnalysisFailureAsync(string operation, Exception error)
    {
        services.Intelligence.RecordAnalysisLog(operation + "：" + error);
        await FlushAnalysisLogQuietlyAsync();
    }
    private async Task FlushAnalysisLogQuietlyAsync()
    {
        try
        {
            await services.Intelligence.FlushAnalysisLogAsync();
            analysisLogStorageFailureShown = false;
        }
        catch (Exception error)
        {
            // 存储失败不递归写日志；应用服务保留原批次，状态栏只报告一次并保留异常原文。
            if (!analysisLogStorageFailureShown)
            {
                analysisLogStorageFailureShown = true;
                Status.Text = "语言分析日志暂时无法保存，原始消息保留待重试。";
                Status.ToolTip = error.ToString();
            }
        }
    }
    private async void AnalysisLog_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var window = new AnalysisLogWindow(services.Intelligence) { Owner = this };
        await window.RefreshAsync(token);
        window.Show();
    });
}
