namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderMx251SettingsPreviewAsync(string output, string project)
    {
        var checks = new List<string>();
        void Check(bool ok, string text)
        {
            if (!ok)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add("PASS " + text);
        }
        projectDirectory = project;
        SetProjectDetailsMode(await ProjectService.ReadAsync(project));
        await ShowProjectDetailsAsync();
        Check(Mx251ModelPanel.Visibility == Visibility.Visible && BuildOptimizationPicker.Visibility == Visibility.Collapsed && BuildDebugInfoPicker.Visibility == Visibility.Collapsed, "MX251 model replaces generic compiler controls");
        Check(Mx251ModelPicker.SelectedValue as string == "small", "saved small model selected");
        Mx251ModelPicker.SelectedValue = "large";
        Check(SaveBuildSettingsButton.IsEnabled, "model change enables save");
        await SaveMx251SettingsAsync(project, default);
        await ShowProjectDetailsAsync();
        Check(Mx251ModelPicker.SelectedValue as string == "large" && !SaveBuildSettingsButton.IsEnabled, "saved large model survives reopening details");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(BuildSettingsCard, Path.Combine(output, "model-settings-" + theme.Id + ".png"));
        }
        var source = await services.Files.ReadAsync(project, Mx251Profile.ConfigurationFile);
        ShowSource(source);
        FindEditor(Mx251Profile.ConfigurationFile)!.Buffer.Text += "\n";
        Mx251ModelPicker.SelectedValue = "small";
        try
        {
            await SaveMx251SettingsAsync(project, default);
            throw new Exception("Dirty config overwritten");
        }
        catch (StudioXException ex) when (ex.Code == "MX251_SETTINGS_UNSAVED") { checks.Add("PASS unsaved config protected"); }
        FindEditor(Mx251Profile.ConfigurationFile)!.Buffer.Text = source.Text;
        ResetBuildSettings_Click(this, new RoutedEventArgs());
        Check(Mx251ModelPicker.SelectedValue as string == "small", "reset selects creation default");
        await SaveMx251SettingsAsync(project, default);
        SetProjectDetailsMode(currentProjectManifest! with
        {
            ToolsetId = "stc.sdcc",
            CompilerId = "sdcc-4.5.0-15242",
            DeviceId = "IAP15L2K61S2"
        });
        Check(Mx251ModelPanel.Visibility == Visibility.Collapsed && BuildOptimizationPicker.Visibility == Visibility.Visible && StcCodeRomPanel.Visibility == Visibility.Visible, "STC8 compiler controls remain available");
        await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), checks);
    }
}
