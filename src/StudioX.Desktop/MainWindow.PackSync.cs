namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;

public partial class MainWindow
{
    private CancellationTokenSource? packSyncCancellation;
    private Task packSyncTask = Task.CompletedTask;

    // 网络同步独立于工作台的 RunAsync：离线 GitHub 不能阻塞编辑、编译和本地器件包。
    public void StartAutomaticPackSync()
    {
        if (!closing && packSyncCancellation is null) StartPackSync(automatic: true);
    }

    private void SyncPacks_Click(object sender, RoutedEventArgs e)
    {
        ShowDocument(PackagesTab);
        if (closing || packSyncCancellation is not null) return;
        StartPackSync(automatic: false);
    }

    private void StartPackSync(bool automatic)
    {
        var cancellation = new CancellationTokenSource();
        packSyncCancellation = cancellation;
        PackSyncButton.IsEnabled = false;
        SyncPacksMenu.IsEnabled = false;
        PackSyncCancelButton.IsEnabled = true;
        PackSyncProgressBar.Visibility = Visibility.Visible;
        PackSyncProgressBar.IsIndeterminate = true;
        PackSyncStatus.Text = automatic ? "正在后台检查 GitHub 器件包…" : "正在从 GitHub 同步器件包…";
        packSyncTask = RunPackSyncAsync(cancellation);
    }

    private async Task RunPackSyncAsync(CancellationTokenSource cancellation)
    {
        try
        {
            var progress = new Progress<RemotePackSyncProgress>(report =>
            {
                if (closing || cancellation.IsCancellationRequested || !ReferenceEquals(packSyncCancellation, cancellation)) return;
                PackSyncProgressBar.IsIndeterminate = report.Total <= 0;
                if (report.Total > 0)
                {
                    PackSyncProgressBar.Maximum = report.Total;
                    PackSyncProgressBar.Value = Math.Clamp(report.Processed, 0, report.Total);
                }
                var count = report.Total > 0 ? $" {report.Processed}/{report.Total}" : "";
                var path = string.IsNullOrWhiteSpace(report.CurrentPath) ? "" : $" · {report.CurrentPath}";
                PackSyncStatus.Text = $"{report.Stage}{count}{path} · 新增 {report.Imported}，已存在 {report.Skipped}，失败 {report.Failed}";
            });
            var result = await services.RemotePacks.SyncAsync(progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            // 刷新选择器前等已有工作台操作结束，避免与新建工程时的本地目录读取交错。
            await pendingOperation.WaitAsync(cancellation.Token);
            if (closing) return;
            await RefreshPacksAsync(cancellation.Token, preserveSelection: true);
            var summary = $"GitHub 器件包同步完成：新增 {result.Imported}，已存在 {result.Skipped}，失败 {result.Failures.Count}。";
            PackSyncStatus.Text = summary;
            Log(summary);
            foreach (var failure in result.Failures)
                Log($"GitHub 器件包同步失败：{failure.Path}：{failure.Message}");
            if (result.Failures.Count > 0)
                PackSyncStatus.Text += " 详情见构建日志；可稍后重试。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!closing) PackSyncStatus.Text = "已取消 GitHub 器件包同步；已有器件包仍可使用。";
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                PackSyncStatus.Text = "GitHub 器件包同步失败，正在使用本地已安装的器件包。可稍后重试：" + ex.Message;
                Log("GitHub 器件包同步失败：" + ex);
            }
        }
        finally
        {
            if (ReferenceEquals(packSyncCancellation, cancellation))
            {
                packSyncCancellation = null;
                PackSyncButton.IsEnabled = true;
                SyncPacksMenu.IsEnabled = true;
                PackSyncCancelButton.IsEnabled = false;
                PackSyncProgressBar.Visibility = Visibility.Collapsed;
            }
            cancellation.Dispose();
        }
    }

    private void CancelPackSync_Click(object sender, RoutedEventArgs e) => packSyncCancellation?.Cancel();

    private async Task StopPackSyncAsync()
    {
        packSyncCancellation?.Cancel();
        await packSyncTask;
    }
}
