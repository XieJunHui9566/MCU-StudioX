namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.StcDebugging;
using StudioX.Engine;

public partial class MainWindow
{
    private async Task CheckLowVoltageMon51UiAsync(string directory, List<string> checks)
    {
        var original = currentProjectManifest!;
        currentProjectManifest = original with
        {
            DeviceId = StcMon51Targets.LowVoltageDevice
        };
        ConfigureBuildSettingsForProject();
        ApplyBuildSettings(new());
        StcMon51Profile.IsChecked = true;
        SelectedBuildSettings.ValidateFor(currentProjectManifest);
        if (StcMon51Profile.Visibility != Visibility.Visible || !SelectedBuildSettings.Mon51Profile)
        {
            throw new InvalidOperationException("L project does not expose Mon51 build profile.");
        }
        checks.Add("L project exposes validated native Mon51 build settings");
        var lowFirmware = Mon51PreviewFirmware with
        {
            PackVersion = "0.1.6",
            DeviceId = StcMon51Targets.LowVoltageDevice,
            FirmwareId = "stc.mon51.iap15l2k61s2",
            ImageSha256 = StcMon51Targets.LowVoltageSetupSha256
        };
        var connection = new Mon51ConnectionWindow(["COM14"], "COM14", "iap15l2k61s2_preview",
            [Mon51PreviewFirmware, lowFirmware],
            (_, _, _, _, _) => throw new InvalidOperationException("UI preview must not connect hardware"),
            (_, _, _, _, _) => throw new InvalidOperationException("UI preview must not program hardware"),
            projectDeviceId: StcMon51Targets.LowVoltageDevice)
        {
            Owner = this,
            ShowActivated = false,
            ShowInTaskbar = false,
            Left = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        connection.Show();
        try
        {
            var body = (StackPanel)((Grid)connection.Content).Children.OfType<ScrollViewer>().Single().Content;
            var selectors = body.Children.OfType<ComboBox>().ToArray();
            if (selectors[0].Items.Count != 1 || (string)selectors[0].Items[0] != StcMon51Targets.LowVoltageDevice ||
                selectors[2].Items.Count != 1 || selectors[2].SelectedItem as Mon51FirmwareSource != lowFirmware)
            {
                throw new InvalidOperationException("L connection window allowed a mismatched F model or monitor.");
            }
            checks.Add("L connection window limits confirmation and firmware to exact L identity");
            if (!body.Children.OfType<TextBlock>().Any(text => text.Text.Contains("7.2.5S / 状态 70 已有实板证据") && text.Text.Contains("其他组合未验证")))
            {
                throw new InvalidOperationException("L hardware evidence must identify its verified ISP combination.");
            }
            checks.Add("L connection window explains bounded hardware evidence and voltage range");
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme);
                connection.UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Render(connection, Path.Combine(directory, "iap15l2k61s2-connection-" + theme.Id + ".png"));
            }
        }
        finally
        {
            connection.Close();
            currentProjectManifest = original;
            ConfigureBuildSettingsForProject();
        }
    }
}
