namespace StudioX.Desktop;

using StudioX.Engine;

public partial class MainWindow
{
    private async Task CheckLowVoltageMon51UiAsync(string directory, List<string> checks)
    {
        var original = currentProjectManifest!;
        try
        {
            currentProjectManifest = original with
            {
                DeviceId = StcMon51Targets.LowVoltageDevice
            };
            ConfigureBuildSettingsForProject();
            ApplyBuildSettings(new());
            StcMon51Profile.IsChecked = true;
            SelectedBuildSettings.ValidateFor(currentProjectManifest);
            if (StcMon51Profile.Visibility != System.Windows.Visibility.Visible || !SelectedBuildSettings.Mon51Profile)
            {
                throw new InvalidOperationException("L project does not expose Mon51 build profile.");
            }
            checks.Add("L project exposes validated native Mon51 build settings");
            await CheckMon51PortSettingsUiAsync(directory, checks);
        }
        finally
        {
            currentProjectManifest = original;
            ConfigureBuildSettingsForProject();
        }
    }
}
