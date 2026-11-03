namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;

public partial class MainWindow
{
    private async Task CheckMon51PortSettingsUiAsync(string directory, List<string> checks)
    {
        var originalSize = System.Windows.Application.Current.Resources["WorkbenchFontSize"];
        ConfigureStcIspForProject();
        ApplyStcIspSettings(new(Port: "COM14", TransferBaud: 57600),
            new(currentProjectManifest!.DeviceId, [StcClockMode.Preserve], false, 0, 0, ""),
            new(false, null, null, "离线预览，不连接串口"), ["COM14"]);
        FocusStcSettings();
        StcPortPicker.BringIntoView();
        CurrentProjectName.Text = "Mon51Preview";
        CurrentProjectDeviceId.Text = currentProjectManifest.DeviceId;
        if (!TrySelectedStcIspSettings(out var settings, out _) || settings is not { Port: "COM14", TransferBaud: 57600 } ||
            !StcIspStatus.Text.Contains("共用此串口和波特率"))
        {
            throw new InvalidOperationException("Mon51 settings do not reuse the download port and baud.");
        }
        checks.Add("Mon51 uses the existing device/template download port and baud controls without a connection dialog");
        try
        {
            var custom = ThemeService.Dark with
            {
                Id = "mon51-custom",
                Colors = new(ThemeService.Dark.Colors)
            };
            custom.Colors["Accent"] = "#B8E986";
            custom.Colors["OnAccent"] = "#163020";
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light, custom })
            {
                ApplyTheme(theme);
                System.Windows.Application.Current.Resources["WorkbenchFontSize"] = theme == custom ? 18d : 13d;
                UpdateLayout();
                StcPortPicker.BringIntoView();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                if (StcIspPanel.Visibility != Visibility.Visible || !StcDownloadTab.IsSelected ||
                    StcPortPicker.ActualWidth < 100 || StcBaudPicker.ActualHeight < 30)
                {
                    throw new InvalidOperationException("Shared Mon51 settings are hidden or clipped.");
                }
                Render(this, Path.Combine(directory, "mon51-download-settings-" + currentProjectManifest.DeviceId + "-" + theme.Id + ".png"));
                checks.Add(theme.Id + " shared Mon51 download settings remain usable");
            }
        }
        finally
        {
            System.Windows.Application.Current.Resources["WorkbenchFontSize"] = originalSize;
            ApplyTheme(ThemeService.Dark);
            ShowDocument(activeEditor?.Tab ?? WelcomeTab);
        }
    }
}
