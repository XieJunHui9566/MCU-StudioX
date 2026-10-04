namespace StudioX.Desktop;

using StudioX.Application.Serial;
using StudioX.Foundation;

public partial class MainWindow
{
    private bool IsEspressifProject => currentProjectManifest?.Espressif is not null;

    private async Task<bool> ShowEspressifDownloadSettingsAsync(string root, CancellationToken token)
    {
        var configuration = await services.EspressifDownloads.ConfigurationAsync(root, token)
            ?? throw new StudioXException("ESP_FLASH_PROJECT", "当前工程不是 Espressif 工程。");
        var ports = await SerialTerminalService.ListPortsAsync();
        var dialog = new EspressifDownloadWindow(configuration, ports) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Settings is not { } settings)
        {
            return false;
        }
        await services.EspressifDownloads.SaveSettingsAsync(root, settings, token);
        Status.Text = "已保存当前 ESP 工程的 COM 端口与下载波特率。";
        return true;
    }

    private async Task DownloadEspressifAsync(string root, CancellationToken token)
    {
        var settings = await services.EspressifDownloads.LoadSettingsAsync(root, token);
        if (settings.Port.Length == 0)
        {
            if (!await ShowEspressifDownloadSettingsAsync(root, token))
            {
                return;
            }
            settings = await services.EspressifDownloads.LoadSettingsAsync(root, token);
        }
        settings.Validate(requirePort: true);
        await SaveAllSourcesAsync(root, token);
        ShowBottom(0);
        BuildLog.Clear();
        var build = await BuildWithSummaryAsync(root, token);
        if (build.LogPath is { } buildLog)
        {
            Log("构建日志：" + buildLog);
        }
        Log(build.Summary);
        if (!build.Success)
        {
            Status.Text = "ESP 编译失败，未打开 COM 端口。";
            return;
        }
        var preview = await services.EspressifDownloads.PreviewAsync(root, settings, token);
        var review = new EspressifFlashReviewWindow(preview) { Owner = this };
        if (review.ShowDialog() != true)
        {
            Status.Text = "已取消 ESP 下载，未打开 COM 端口。";
            return;
        }
        Status.Text = "正在下载 ESP 多映像并逐个校验…";
        var report = await services.EspressifDownloads.DownloadApprovedAsync(root, settings,
            preview.Configuration.Device.Id, preview.Layout.LayoutSha256,
            new Progress<string>(text => Log(text.TrimEnd('\r', '\n'))), token);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Log("下载日志：" + report.LogPath);
        Log(report.Summary);
        Status.Text = report.Summary;
    }
}
