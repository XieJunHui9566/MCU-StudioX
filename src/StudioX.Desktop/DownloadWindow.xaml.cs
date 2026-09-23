namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudioX.Engine;
using StudioX.Packages;

public partial class DownloadWindow : Window
{
    public DownloadOptions? Options { get; private set; }
    public DownloadWindow(DownloadConfiguration configuration)
    {
        InitializeComponent();
        TargetLabel.Text = configuration.Device.DisplayName;
        ProbePicker.ItemsSource = configuration.OpenOcd.Probes;
        ProbePicker.SelectedValue = configuration.Options.ProbeId;
        SpeedInput.Text = configuration.Options.SpeedKhz.ToString(CultureInfo.InvariantCulture);
        SerialInput.Text = configuration.Options.Serial ?? "";
    }
    private void Probe_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedInput is not null && ProbePicker.SelectedItem is DebugProbeDefinition probe)
        {
            SpeedInput.Text = probe.DefaultSpeedKhz.ToString(CultureInfo.InvariantCulture);
            SerialInput?.Clear();
            if (SerialInput is not null) SerialInput.IsEnabled = probe.Transport != "sdi";
            if (SpeedLabel is not null) SpeedLabel.Text = probe.Transport == "sdi" ? "SDI 速度（400 / 4000 / 6000 kHz，单台 WCH-Link）" : "调试接口速度（kHz）";
        }
    }
    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (ProbePicker.SelectedItem is not DebugProbeDefinition probe || !int.TryParse(SpeedInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var speed) || speed is < 100 or > 15000)
        { ErrorLabel.Text = "请选择烧录器，并输入 100–15000 kHz 的速度。"; return; }
        if (SerialInput.Text.Length > 100 || SerialInput.Text.Any(char.IsControl)) { ErrorLabel.Text = "序列号格式无效。"; return; }
        if (probe.Transport == "sdi" && speed is not (400 or 4000 or 6000)) { ErrorLabel.Text = "WCH-Link 速度请选择 400、4000 或 6000 kHz。"; return; }
        Options = new(probe.Id, speed, SerialInput.Text.Trim());
        DialogResult = true;
    }
}
