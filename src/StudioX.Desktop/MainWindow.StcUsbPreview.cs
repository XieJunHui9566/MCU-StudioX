namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;

public partial class MainWindow
{
    /// <summary>隔离 USB 下载设置预览；不列举设备、不构建、不连接硬件。</summary>
    public async Task RenderStcUsbPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        var originalSize = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        var custom = ThemeService.Dark with
        {
            Id = "usb-custom",
            Colors = new(ThemeService.Dark.Colors)
        };
        custom.Colors["Accent"] = "#B8E986";
        custom.Colors["OnAccent"] = "#163020";
        projectDirectory = directory;
        currentProjectManifest = new(1, "USB设置离线预览", "stc.stc8", "0.1.8", "", "STC8H8K64U", "bare", "stc.sdcc", "1.0.0", "sdcc-4.5.0-15242");
        var capabilities = new StcIspCapabilities("STC8H8K64U", [StcClockMode.Preserve, StcClockMode.InternalRc], true, 4000000, 36000000, "");
        var settings = new StcIspSettings(Port: "COM14", TransferBaud: 57600);
        try
        {
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
            {
                ApplyTheme(theme);
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = theme == custom ? 18d : 13d;
                ConfigureStcIspForProject();
                loadedProjectClock = null;
                ApplyStcIspSettings(settings, capabilities, new(false, null, null, "离线预览，无外部工具"), ["COM14"]);
                Check(StcDownloadModePicker.Items.Count == 2 && StcSerialFields.Visibility == Visibility.Visible, theme.Id + ": UART and native USB choices");
                StcPortPicker.Text = "COM99";
                StcClockModePicker.SelectedValue = StcClockMode.InternalRc;
                StcClockFrequencyBox.Text = "24";
                StcDownloadModePicker.SelectedValue = StcDownloadMode.NativeUsb;
                Check(StcPortPicker.Text == "COM99" && StcClockFrequencyBox.Text == "24" && loadedStcIspSettings == settings,
                    theme.Id + ": changing mode preserves unsaved port and clock drafts");
                Check(StcSerialFields.Visibility == Visibility.Collapsed && ChooseStcgalButton.Visibility == Visibility.Collapsed &&
                    StcUsbHelp.Visibility == Visibility.Visible && StcToolStatus.Visibility == Visibility.Collapsed,
                    theme.Id + ": USB hides COM, baud and external programmer, shows auto-discovery help");
                Check(TrySelectedStcIspSettings(out var selected, out _) && selected.DownloadMode == StcDownloadMode.NativeUsb && selected.ClockFrequencyHz == 24000000,
                    theme.Id + ": valid USB RC24 draft");
                StcClockFrequencyBox.Text = "12";
                Check(!TrySelectedStcIspSettings(out _, out var error) && error.Contains("24 MHz", StringComparison.Ordinal), theme.Id + ": unsupported USB frequency explained inline");
                StcClockFrequencyBox.Text = "24";
                FocusStcSettings();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                StcIspPanel.BringIntoView();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                UpdateLayout();
                Check(StcUsbHelp.IsVisible && StcDownloadModePicker.IsVisible, theme.Id + ": actual settings rendered");
                Render(this, Path.Combine(directory, "usb-" + theme.Id + ".png"));
                StcDownloadModePicker.SelectedValue = StcDownloadMode.Serial;
                Check(StcPortPicker.Text == "COM99" && StcSerialFields.Visibility == Visibility.Visible,
                    theme.Id + ": UART draft restored without loading saved values");
                ApplyStcIspSettings(new(), capabilities with
                {
                    DeviceId = "STC8H1K17"
                }, new(false, null, null, "离线"), []);
                Check(StcDownloadModePicker.Items.Count == 1, theme.Id + ": unsupported device has UART only");
            }
        }
        finally
        {
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = originalSize;
            ApplyTheme(ThemeService.Dark);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), $"PASS {checks.Count} USB UI checks; no hardware access.\n" + string.Join('\n', checks));
    }
}
