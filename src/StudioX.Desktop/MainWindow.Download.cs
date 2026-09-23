namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Engine;
using StudioX.Packages;

public partial class MainWindow
{
    private bool supportsDownload;
    private bool updatingDownloadPicker;
    private DownloadConfiguration? downloadConfiguration;

    private void ApplyDownloadConfiguration(DownloadConfiguration? configuration)
    {
        // STC 使用串口 ISP；OpenOCD 烧录器列表与其互不通用。
        if (IsStcSdccProject) configuration = null;
        updatingDownloadPicker = true;
        try
        {
            downloadConfiguration = configuration;
            supportsDownload = IsStcSdccProject || configuration is not null;
            DownloadProbeLabel.Visibility = DownloadProbePicker.Visibility = IsStcSdccProject ? Visibility.Collapsed : Visibility.Visible;
            DownloadProbePicker.ItemsSource = configuration?.OpenOcd.Probes;
            DownloadProbePicker.SelectedValue = configuration?.Options.ProbeId;
            var probe = configuration?.OpenOcd.Probes.Single(p => p.Id == configuration.Options.ProbeId);
            DownloadButton.ToolTip = IsStcSdccProject ? "保存、编译并通过 STC 串口 ISP 下载；请先在工程设置中配置 COM 口和时钟" :
                configuration is null ? "当前器件尚未提供匹配的下载配置" :
                $"保存、编译并下载 · {probe!.DisplayName} · {configuration.Options.SpeedKhz} kHz";
            DownloadSettingsButton.ToolTip = IsStcSdccProject ? "STC 串口、时钟与下载设置" : "下载设置：速度与序列号";
            DownloadProbePicker.ToolTip = configuration is null ? "打开工程后选择该器件支持的烧录器" :
                $"{configuration.Device.Id} · {probe!.Transport.ToUpperInvariant()} · {configuration.Options.SpeedKhz} kHz\n选择按当前工程保存；速度与序列号可在下载设置中修改。";
        }
        finally { updatingDownloadPicker = false; }
        RefreshDebugUi();
        UpdateProjectActions(projectActionsBusy);
    }

    private async void DownloadProbe_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updatingDownloadPicker || downloadConfiguration is not { } configuration ||
            DownloadProbePicker.SelectedItem is not DebugProbeDefinition probe || probe.Id == configuration.Options.ProbeId) return;
        await RunAsync(async token =>
        {
            try
            {
                // 更换烧录器时，旧烧录器的序列号和速度不能带到新驱动。
                var options = new DownloadOptions(probe.Id, probe.DefaultSpeedKhz);
                await services.Downloads.SaveOptionsAsync(RequireProject(), options, token);
                downloadConfiguration = configuration with { Options = options };
                Status.Text = "烧录器已切换为 " + probe.DisplayName;
            }
            finally { ApplyDownloadConfiguration(downloadConfiguration); }
        });
    }

    private async void DownloadSettings_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        var root = RequireProject();
        if (IsStcSdccProject)
        {
            if (loadedStcIspSettings is null) await ShowProjectDetailsAsync();
            else FocusStcSettings();
            return;
        }
        var configuration = await services.Downloads.ConfigurationAsync(root, token);
        if (configuration is null) return;
        var dialog = new DownloadWindow(configuration) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Options is not { } options) return;
        await services.Downloads.SaveOptionsAsync(root, options, token);
        ApplyDownloadConfiguration(configuration with { Options = options });
        Status.Text = "已保存当前工程的下载设置。";
    });

    private async void Download_Click(object sender, RoutedEventArgs e) => await RunAsync(async token =>
    {
        EnsureNoActiveDebug();
        var root = RequireProject();
        if (IsStcSdccProject) { await DownloadStcAsync(root, token); return; }
        var configuration = await services.Downloads.ConfigurationAsync(root, token);
        if (configuration is null) { Status.Text = "当前器件尚未提供匹配的下载配置。"; return; }
        ApplyDownloadConfiguration(configuration);
        var options = configuration.Options;
        await SaveAllSourcesAsync(root, token);
        ShowBottom(0); BuildLog.Clear();
        var output = new Progress<string>(text => Log(text.TrimEnd('\r', '\n')));
        var build = await BuildWithSummaryAsync(root, token);
        if (build.LogPath is { } logPath) Log("构建日志：" + logPath);
        Log(build.Summary);
        if (!build.Success) { Status.Text = "编译失败，未启动下载。"; return; }
        Status.Text = "正在下载与校验…";
        var result = await services.Downloads.DownloadAsync(root, options, output, token);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Log("下载日志：" + result.LogPath);
        Log(result.Summary);
        Status.Text = result.Summary;
    });
}
