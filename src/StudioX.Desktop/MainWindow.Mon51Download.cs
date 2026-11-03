namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application.StcDebugging;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    private string Mon51UnavailableMessage => $"{currentProjectManifest?.DeviceId} 未接入 Mon51 串口仿真；当前仅支持 {StcMon51Targets.DisplayNames}。";

    private async Task StartMon51DebugAsync(CancellationToken token)
    {
        var root = RequireProject();
        // 禁用按钮之外仍保护直接命令调用，必须在读取设置、自动保存及连接之前拒绝。
        if (!ProjectCapabilities.For(currentProjectManifest).Debug)
        {
            Status.Text = Mon51UnavailableMessage;
            return;
        }
        // 调试预设读取已保存配置并回填表单；必须先保护草稿，缺少串口时也不能重载覆盖。
        if (loadedBuildSettings is not null &&
            (!TrySelectedBuildSettings(out var selectedBuild, out var buildError) || selectedBuild != loadedBuildSettings))
        {
            FocusStcBuildSettings();
            BuildSettingsStatus.Text = Status.Text = buildError.Length > 0 ? buildError : "编译参数尚未保存；请先保存后再调试。";
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (projectDirectory == root && (PackagesTab.IsSelected || StcDownloadTab.IsSelected))
                {
                    if (StcDownloadTab.IsSelected) { StcBuildSettingsStatus.BringIntoView(); }
                    else { BuildSettingsStatus.BringIntoView(); }
                }
            }, DispatcherPriority.Loaded);
            return;
        }
        if (loadedStcIspSettings is not null &&
            (!TrySelectedStcIspSettings(out var selected, out var error) || selected != loadedStcIspSettings))
        {
            FocusStcSettings();
            StcPortPicker.BringIntoView();
            StcIspStatus.Text = error.Length > 0 ? error : "串口或时钟参数尚未保存；请保存后再次点击调试。";
            return;
        }
        var settings = await services.StcIsp.LoadSettingsAsync(root, token);
        if (string.IsNullOrWhiteSpace(settings.Port))
        {
            await ShowMon51PortSettingsAsync();
            return;
        }
        await SaveAllSourcesAsync(root, token);
        await PersistBreakpointLinesAsync();
        ApplyBuildSettings(await services.StcBuilds.PrepareDebugAsync(root, token));
        ShowBottom(0);
        BuildLog.Clear();
        var progress = new Progress<string>(message =>
        {
            foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
            {
                Log(line);
            }
        });
        var result = await services.Mon51Launch.StartAsync(root, async cancel =>
        {
            var build = await BuildWithSummaryAsync(root, cancel);
            if (build.LogPath is { } logPath)
            {
                Log("构建日志：" + logPath);
            }
            Log(build.Summary);
            if (!build.Success)
            {
                throw new StudioXException("MON51_BUILD", "最新源码编译失败，未覆盖目标程序。\n" + build.Summary);
            }
            var preparation = await services.Debugger.PrepareMon51DownloadAsync(cancel);
            Log($"用户程序 {preparation.UserBytes} 字节；BIN SHA-256：{preparation.BinarySha256}\n正在通过 Mon51 更新用户区并回读核对…");
            if (editorDocuments.Any(document => document.IsDirty))
            {
                throw new StudioXException("MON51_SOURCE_CHANGED", "编译期间出现未保存编辑，请重新开始调试；未覆盖目标。");
            }
            return preparation;
        }, progress, token, (prepared, cancel) => RunStcDownloadWithOutputAsync(prepared,
            (output, downloadToken) => services.Mon51Setup.ExecuteAsync(prepared, output, downloadToken), cancel));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        RefreshDebugUi();
        if (result == Mon51LaunchResult.PortRequired)
        {
            await ShowMon51PortSettingsAsync();
            return;
        }
        Status.Text = result == Mon51LaunchResult.MonitorInstalled
            ? "仿真监控已下载，请再次断电上电后重新点击调试 · 本次调试已退出"
            : services.Debugger.Reason;
    }

    private async Task ShowMon51PortSettingsAsync()
    {
        await ShowStcDownloadSettingsAsync(RequireProject(), CancellationToken.None);
        StcPortPicker.BringIntoView();
        StcPortPicker.Focus();
        StcIspStatus.Text = Status.Text = "请选择并保存下载串口；串口仿真复用此串口和传输波特率，然后再次点击调试。";
    }
}
