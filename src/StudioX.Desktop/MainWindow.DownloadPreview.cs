namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>只操作离线验证工具生成的工程和设置；不点击下载、不接触硬件。</summary>
    public async Task RenderDownloadPreviewAsync(string directory, string fixtures)
    {
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        var native = Path.Combine(fixtures, "native-f407");
        var cube = Path.Combine(fixtures, "cube-f407 with spaces");
        await OpenFromCommandLineAsync(native);
        Check(DownloadButton.IsEnabled && DownloadProbePicker.Items.Count == 3, "Native probes unavailable: " + Status.Text);
        foreach (var id in new[] { "cmsis-dap", "jlink", "stlink", "jlink" })
        {
            DownloadProbePicker.SelectedValue = id; await pendingOperation;
            Check((await services.Downloads.ConfigurationAsync(native))!.Options.ProbeId == id, "Toolbar selection not saved");
        }
        await services.Downloads.SaveOptionsAsync(native, new("jlink", 1250, "probe-123"));
        ApplyDownloadConfiguration(await services.Downloads.ConfigurationAsync(native));
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(MainToolbar, Path.Combine(directory, "toolbar-" + theme.Id + ".png"));
            DownloadProbePicker.IsDropDownOpen = true; UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            if (DownloadProbePicker.Template.FindName("PART_Popup", DownloadProbePicker) is Popup { Child: FrameworkElement popup })
            { popup.UpdateLayout(); Render(popup, Path.Combine(directory, "probes-" + theme.Id + ".png")); }
            DownloadProbePicker.IsDropDownOpen = false;
            var dialog = new DownloadWindow(downloadConfiguration!) { Owner = this, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -18000 };
            dialog.Show(); dialog.UpdateLayout();
            Check(((TextBox)dialog.FindName("SpeedInput")).Text == "1250" && ((TextBox)dialog.FindName("SerialInput")).Text == "probe-123", "Settings not restored");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(dialog, Path.Combine(directory, "settings-" + theme.Id + ".png")); dialog.Close();
        }
        UpdateProjectActions(true);
        Check(!DownloadButton.IsEnabled && !DownloadProbePicker.IsEnabled && !DownloadSettingsButton.IsEnabled, "Download controls active while busy");
        UpdateProjectActions(false);
        await CloseProjectAsync(CancellationToken.None);
        Check(!DownloadButton.IsEnabled && DownloadProbePicker.Items.Count == 0 && !DownloadProbePicker.IsEnabled, "Close did not clear probes");
        await OpenFromCommandLineAsync(cube);
        Check(DownloadButton.IsEnabled && DownloadProbePicker.Items.Count == 3 && (string)DownloadProbePicker.SelectedValue == "stlink", "CubeMX settings leaked");
        DownloadProbePicker.SelectedValue = "cmsis-dap"; await pendingOperation;
        await OpenFromCommandLineAsync(native);
        Check((string)DownloadProbePicker.SelectedValue == "jlink" && downloadConfiguration!.Options.SpeedKhz == 1250 && downloadConfiguration.Options.Serial == "probe-123", "Project settings lost");
        DownloadProbePicker.SelectedValue = "cmsis-dap"; await pendingOperation;
        Check(downloadConfiguration!.Options.Serial is null && downloadConfiguration.Options.SpeedKhz == 2000, "Serial carried to a different adapter");
        await OpenFromCommandLineAsync(Path.Combine(fixtures, "native-ag32"));
        Check(!DownloadButton.IsEnabled && !DownloadProbePicker.IsEnabled && DownloadProbePicker.Items.Count == 0, "Pack without download configuration inherited the previous project's probes");
        await OpenFromCommandLineAsync(cube);
        Check((string)DownloadProbePicker.SelectedValue == "cmsis-dap", "CubeMX selection lost on reopen");
        ApplyTheme(ThemeService.Dark); Width = 1100; UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var right = CancelButton.TranslatePoint(new Point(CancelButton.ActualWidth, 0), MainToolbar).X;
        Check(right < MainToolbar.ActualWidth - 76, "Toolbar actions overlap appearance button at minimum width");
        Render(MainToolbar, Path.Combine(directory, "toolbar-minimum.png"));
        Width = 1460; UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "workspace.png"));
        await CloseProjectAsync(CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: native STM32, CubeMX, AG32 toolbar; 3 probe selections; settings roundtrip; per-project persistence/reopen; clear serial on probe change; busy/close state; dark/light settings/dropdown; 1100px toolbar. No hardware accessed.\n");
    }
}
