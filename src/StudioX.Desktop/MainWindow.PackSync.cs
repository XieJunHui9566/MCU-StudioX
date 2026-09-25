namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;

public partial class MainWindow
{
    private CancellationTokenSource? packSyncCancellation;
    private Task packSyncTask = Task.CompletedTask;

    private void CheckPackUpdates_Click(object sender, RoutedEventArgs e)
    {
        ShowDocument(PackagesTab);
        if (closing || packSyncCancellation is not null) return;
        StartPackOperation(checkOnly: true);
    }

    private void SyncPacks_Click(object sender, RoutedEventArgs e)
    {
        ShowDocument(PackagesTab);
        if (closing || packSyncCancellation is not null) return;
        StartPackOperation(checkOnly: false);
    }

    // 网络操作仅由按钮触发，独立于 RunAsync，不影响编辑、编译和本地器件包。
    private void StartPackOperation(bool checkOnly)
    {
        var cancellation = new CancellationTokenSource();
        packSyncCancellation = cancellation;
        PackCheckButton.IsEnabled = false;
        PackSyncButton.IsEnabled = false;
        CheckPackUpdatesMenu.IsEnabled = false;
        SyncPacksMenu.IsEnabled = false;
        PackSyncCancelButton.IsEnabled = true;
        PackSyncProgressBar.Visibility = Visibility.Visible;
        PackSyncProgressBar.IsIndeterminate = true;
        PackSyncStatus.Text = checkOnly ? "正在检查 GitHub 器件包更新…" : "正在从 GitHub 同步下载器件包…";
        packSyncTask = checkOnly ? RunPackCheckAsync(cancellation) : RunPackSyncAsync(cancellation);
    }

    private async Task RunPackCheckAsync(CancellationTokenSource cancellation)
    {
        try
        {
            var result = await services.RemotePacks.CheckForUpdatesAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (closing) return;
            var summary = result.Updates.Count == 0
                ? "GitHub 器件包已是最新；未发现需要下载的新版本。"
                : $"发现 {result.Updates.Count} 个可同步的器件包；点击“同步下载”导入。";
            PackSyncStatus.Text = summary;
            Log(summary);
            foreach (var update in result.Updates)
                Log($"可更新器件包：{update.Id} · {update.InstalledVersion ?? "未安装"} → {update.Version}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!closing) PackSyncStatus.Text = "已取消 GitHub 器件包更新检查。";
        }
        catch (Exception ex)
        {
            if (!closing)
            {
                PackSyncStatus.Text = "检查更新失败；本地器件包仍可使用。可稍后重试：" + ex.Message;
                Log("GitHub 器件包更新检查失败：" + ex);
            }
        }
        finally { FinishPackOperation(cancellation); }
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
        finally { FinishPackOperation(cancellation); }
    }

    private void FinishPackOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(packSyncCancellation, cancellation))
        {
            packSyncCancellation = null;
            PackCheckButton.IsEnabled = true;
            PackSyncButton.IsEnabled = true;
            CheckPackUpdatesMenu.IsEnabled = true;
            SyncPacksMenu.IsEnabled = true;
            PackSyncCancelButton.IsEnabled = false;
            PackSyncProgressBar.Visibility = Visibility.Collapsed;
        }
        cancellation.Dispose();
    }

    private void CancelPackSync_Click(object sender, RoutedEventArgs e) => packSyncCancellation?.Cancel();

    private async Task StopPackSyncAsync()
    {
        packSyncCancellation?.Cancel();
        await packSyncTask;
    }
}
