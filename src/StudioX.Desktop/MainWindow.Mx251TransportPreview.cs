namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;

public partial class MainWindow
{
    /// <summary>只渲染三种连接设置；不枚举、连接设备或触发构建。</summary>
    public async Task RenderMx251TransportPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks.Add(message); }
        projectDirectory = directory;
        currentProjectManifest = new(1, "AI8051U 下载设置", "stc.ai8051u-mx251", "0.2.171", "", "AI8051U-34K64", "registers", "studiox.mx251", "0.0.170", "mx251-v0.0170");
        var custom = ThemeService.Dark with { Id = "custom-large", Colors = new(ThemeService.Dark.Colors) };
        custom.Colors["Accent"] = "#B8E986"; custom.Colors["OnAccent"] = "#163020";
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
        {
            ApplyTheme(theme);
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = theme == custom ? 18d : 13d;
            ConfigureMx251IspForProject();
            Mx251SerialPortPicker.ItemsSource = new[] { "COM6" };
            ApplyMx251Isp(new(DownloadMode: Mx251DownloadMode.Serial, SerialPort: "COM6"), []);
            Check(Mx251IspPanel.Visibility == Visibility.Visible && Mx251DownloadModePicker.Items.Count == 3, "SDK capability and three transport choices");
            Mx251SerialPortPicker.Text = "COM77";
            Mx251ClockFrequencyBox.Text = "24";
            Mx251DownloadModePicker.SelectedValue = Mx251DownloadMode.NativeUsb;
            Check(Mx251SerialPanel.Visibility == Visibility.Collapsed && Mx251BridgePanel.Visibility == Visibility.Collapsed, "native USB hides serial and bridge selectors");
            Mx251DownloadModePicker.SelectedValue = Mx251DownloadMode.Serial;
            Check(Mx251SerialPanel.Visibility == Visibility.Visible && ReadMx251IspSettings().SerialPort == "COM77" && ReadMx251IspSettings().IrcHz == 24000000, "switch preserves port and frequency drafts");
            Mx251DownloadModePicker.SelectedValue = Mx251DownloadMode.UsbBridge;
            Check(Mx251BridgePanel.Visibility == Visibility.Visible && Mx251SerialPanel.Visibility == Visibility.Collapsed, "bridge selection remains independent");
            Mx251DownloadModePicker.SelectedValue = theme.Id == ThemeService.Light.Id ? Mx251DownloadMode.NativeUsb : Mx251DownloadMode.Serial;
            FocusStcSettings();
            Height = 1050;
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Mx251DownloadModePicker.BringIntoView();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
            Check(Mx251DownloadModePicker.IsVisible, "actual download settings rendered");
            Render(this, Path.Combine(directory, "mx251-" + theme.Id + ".png"));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS " + checks.Count + " transport UI checks; no hardware access.\n" + string.Join('\n', checks));
    }
}
