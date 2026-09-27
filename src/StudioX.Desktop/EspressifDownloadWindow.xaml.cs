namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application.Serial;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>只编辑端口选项；串口发现和固件下载交给应用服务。</summary>
public partial class EspressifDownloadWindow : Window
{
    private readonly Func<Task<string[]>> listPorts;

    public EspressifFlashSettings? Settings
    {
        get; private set;
    }

    public EspressifDownloadWindow(EspressifFlashConfiguration configuration, string[] ports, Func<Task<string[]>>? listPorts = null)
    {
        InitializeComponent();
        this.listPorts = listPorts ?? (() => SerialTerminalService.ListPortsAsync());
        var sdk = configuration.Project.Espressif!;
        TargetLabel.Text = $"{configuration.Device.DisplayName}\n{sdk.Framework} {sdk.SdkVersion} · {sdk.Target}";
        RestorePorts(ports, configuration.Settings.Port);
        BaudRatePicker.ItemsSource = new[] { 115200, 230400, 460800, 921600 };
        BaudRatePicker.SelectedItem = configuration.Settings.BaudRate;
    }

    private async void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshPortsAsync();
            ErrorLabel.Text = "";
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.ToString();
        }
    }

    internal async Task RefreshPortsAsync()
    {
        var ports = await listPorts();
        // 枚举期间仍可编辑端口，完成时保留最新输入，不能恢复点击刷新前的旧值。
        RestorePorts(ports, PortPicker.Text);
    }

    private void RestorePorts(string[] ports, string selected)
    {
        PortPicker.ItemsSource = ports;
        // 端口临时消失时保留用户输入；刷新列表不能隐式改成另一个可用设备。
        var matching = ports.FirstOrDefault(port => port.Equals(selected, StringComparison.OrdinalIgnoreCase));
        PortPicker.SelectedItem = matching;
        // WPF 的可编辑选择框会按文本重新匹配；有匹配项时使用同一规范拼写，避免选中项被清掉。
        PortPicker.Text = matching ?? selected;
    }

    internal EspressifFlashSettings ReadSettings()
    {
        var settings = new EspressifFlashSettings(Port: PortPicker.Text.Trim().ToUpperInvariant(),
            BaudRate: BaudRatePicker.SelectedItem is int baud ? baud : 0);
        settings.Validate(requirePort: true);
        return settings;
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Settings = ReadSettings();
            DialogResult = true;
        }
        catch (StudioXException ex)
        {
            ErrorLabel.Text = ex.Message;
        }
    }
}
