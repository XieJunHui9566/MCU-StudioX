namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Ai8051Isp;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderMx251DownloadPreviewAsync(string output, string project)
    {
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
        await OpenProjectAsync(project, default);
        Check(DownloadButton.IsEnabled && DownloadMenu.IsEnabled && DownloadSettingsButton.IsEnabled, "download and settings enabled for MX251");
        Check(DownloadProbePicker.Visibility == Visibility.Collapsed, "no irrelevant OpenOCD probe selector");
        var originalTab = PackagesTab.IsSelected;
        var originalRevision = projectDetailsRevision;
        var connected = await services.Mx251Downloads.ListDevicesAsync();
        Check(connected.Count == 1, "read-only UI acceptance uses one connected bridge; no programming");
        Check((await services.Mx251Downloads.LoadSettingsAsync(project)).DevicePath.Length == 0, "fresh UI user data has no saved bridge");
        Check(await ResolveMx251DownloadDeviceAsync(project, default), "first-click path resolves without opening settings or a dialog");
        Check(PackagesTab.IsSelected == originalTab && projectDetailsRevision == originalRevision, "first-click resolution preserves current editor tab");
        Check((await services.Mx251Downloads.LoadSettingsAsync(project)).DevicePath == connected[0].Path, "single device selection persisted automatically");
        Check(await ResolveMx251DownloadDeviceAsync(project, default) && projectDetailsRevision == originalRevision, "subsequent click reuses saved bridge without navigation");
        await ShowProjectDetailsAsync();
        Check(Mx251IspPanel.Visibility == Visibility.Visible && StcIspPanel.Visibility == Visibility.Collapsed, "dedicated USB settings in project details");
        Check(Mx251CpuModeBox.IsReadOnly && Mx251CpuModeBox.Text == "32 位（本器件固定）", "CPU mode read-only32; no 8-bit selection");
        Check(!Mx251ClockFrequencyBox.IsReadOnly && Mx251ClockFrequencyBox.Text == "40", "editable IRC defaults to 40 MHz");
        Mx251ClockFrequencyBox.Text = "11.0592";
        Check(ReadMx251IspSettings().IrcHz == 11059200, "fractional MHz input keeps exact Hz");
        await RefreshMx251DevicesAsync(project, default);
        Check(Mx251ClockFrequencyBox.Text == "11.0592", "refresh retains pending frequency");
        Check(await ResolveMx251DownloadDeviceAsync(project, default) &&
            (await services.Mx251Downloads.LoadSettingsAsync(project)).IrcHz == 11059200, "download applies pending clock without navigation");
        await LoadMx251IspAsync(project, projectDetailsRevision);
        Check(Mx251ClockFrequencyBox.Text == "11.0592", "saved frequency survives reload");
        Check(await Mx251ClockConfiguration.ReadAsync(project) == 11059200, "saved UI frequency also binds compilation");
        Mx251ModelPicker.SelectedValue = "large";
        Mx251ClockFrequencyBox.Text = "16";
        await SaveMx251IspSettingsAsync(default);
        await SaveMx251SettingsAsync(project, default);
        Check((await services.Mx251Settings.ReadAsync(project)).Model == "large" &&
            await Mx251ClockConfiguration.ReadAsync(project) == 16000000, "clock save preserves pending model selection and does not create a stale-hash conflict");
        foreach (var invalid in new[] { "", "0", "49", "NaN", "Infinity", "1e1", "24.0000001" })
        {
            Mx251ClockFrequencyBox.Text = invalid;
            bool rejected = false;
            try
            {
                ReadMx251IspSettings();
            }
            catch (StudioXException e) when (e.Code == "MX251_ISP_CLOCK") { rejected = true; }
            Check(rejected, "invalid frequency rejected: " + invalid);
        }
        Mx251ClockFrequencyBox.Text = "24";
        var device = new HidDevice(@"\\?\hid#vid_34bf&pid_ff0f&mi_00#preview", 0x34bf, 0xff0f, "STC USB Serial", 65, 65);
        ApplyMx251Isp(new(device.Path, IrcHz: 24000000), [device]);
        await SaveMx251IspSettingsAsync(default);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Height = 1050;
            UpdateLayout();
            Mx251IspPanel.BringIntoView();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(output, "mx251-download-" + theme.Id + ".png"));
        }
        ApplyMx251Isp(new(device.Path), [device with { Path = device.Path + "-other" }]);
        Check(Mx251DevicePicker.SelectedValue is null, "missing saved bridge not silently replaced");
        ApplyMx251Isp(new(), []);
        Check(Mx251DevicePicker.Items.Count == 0, "disconnected board has no selectable device");
        await CloseProjectAsync(default);
        Check(Mx251IspPanel.Visibility == Visibility.Collapsed && !DownloadButton.IsEnabled, "closing clears download capability and panel");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            checks,
            hardwareOpened = false
        });
    }
}
