namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Tools;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>只读真实 SDK 入口及独立用户状态；不构建、不连接设备、不请求在线目录。</summary>
    public async Task RenderIdfVersionsPreviewAsync(string output, string stage, string originalArchive)
    {
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
        }
        foreach (var (sdk, pack) in new[] { ("5.5.5", "0.2.0"), ("6.0.3", "0.3.0"), ("6.1.0", "0.4.0") })
        {
            await services.Packs.ImportAsync(Path.Combine(stage, "packs-" + sdk, $"espressif.esp32s3-{pack}.mcupack"));
        }
        await services.Packs.ImportAsync(originalArchive);
        await RefreshPacksAsync(CancellationToken.None);
        ShowDocument(PackagesTab);
        SelectPack(installedPacks.Single(item => item.Manifest.Id == "espressif.esp32s3" && item.Manifest.Version == "0.1.1"));
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single(item => item.Id == "ESP32-S3");
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(item => item.Id == "hello-world");
        await RefreshIdfVersionsAsync();
        Check(IdfVersionPanel.Visibility == Visibility.Visible && IdfVersionPicker.Items.Count == 4, "new project exposes four exact IDF choices");
        Check((IdfVersionPicker.SelectedItem as EspressifProjectVersionChoice)?.SdkVersion == "5.5.4", "initial selection preserves explicitly selected pack, not newest SDK");
        var choices = IdfVersionPicker.Items.Cast<EspressifProjectVersionChoice>().ToArray();
        IdfVersionPicker.SelectedItem = choices.Single(item => item.SdkVersion == "6.0.3");
        Check((PackPicker.SelectedItem as InstalledPack)?.Manifest.Version == "0.3.0" && (DevicePicker.SelectedItem as DeviceDefinition)?.Id == "ESP32-S3"
            && (TemplatePicker.SelectedItem as ProjectTemplate)?.Id == "hello-world" && CreateProjectButton.IsEnabled, "selecting SDK switches matching pack while retaining chip and template");
        IdfVersionPicker.SelectedItem = choices.Single(item => item.SdkVersion == "6.1.0");
        Check(SelectedDevelopmentComponents.Text.Contains("6.1.0"), "component requirement summary follows selected SDK");
        var configured = await services.Projects.CreateAsync((InstalledPack)PackPicker.SelectedItem, "ESP32-S3", "hello-world", "ui_selected_idf", Path.Combine(output, "selected-project"));
        Check(configured.Espressif?.SdkVersion == "6.1.0" && configured.ToolsetVersion == "6.1.0", "real new-project service writes selected SDK identity");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Width = 1440;
            Height = 960;
            await Layout();
            IdfVersionPanel.BringIntoView();
            await Layout();
            Check(IdfVersionPicker.ActualWidth > 200 && IdfVersionDescription.ActualHeight > 15, theme.Id + " version and status remain readable");
            Render(this, Path.Combine(output, "idf-version-" + theme.Id + ".png"));
        }
        await services.Toolsets.SetEnabledAsync("espressif.idf", "6.1.0", false);
        try
        {
            await RefreshIdfVersionsAsync();
            Check(IdfVersionPicker.SelectedItem is EspressifProjectVersionChoice { State: EspressifVersionState.Disabled } && !CreateProjectButton.IsEnabled,
                "disabled version is visible and cannot create a new project");
            ApplyTheme(ThemeService.Dark);
            Width = MinWidth;
            Height = 720;
            await Layout();
            IdfVersionPanel.BringIntoView();
            await Layout();
            Render(this, Path.Combine(output, "idf-version-disabled-compact.png"));
            await ShowToolManagementAsync();
            var actual = toolManagementView!.Report!.Versions.Single(item => item.Id == "espressif.idf" && item.Version == "6.1.0");
            toolManagementView.VersionsGrid.SelectedItem = actual;
            Check(toolManagementView.ToggleButton.IsEnabled && Equals(toolManagementView.ToggleButton.Content, "启用此版本") && !toolManagementView.DebugCheckButton.IsEnabled,
                "manager offers enable and blocks debug check for disabled version");
            Width = 1440;
            Height = 960;
            await Layout();
            Render(this, Path.Combine(output, "component-management-disabled.png"));
        }
        finally { await services.Toolsets.SetEnabledAsync("espressif.idf", "6.1.0", true); }
        await RefreshToolManagementAsync(CancellationToken.None);
        toolManagementView!.VersionsGrid.SelectedItem = toolManagementView.Report!.Versions.Single(item => item.Id == "espressif.idf" && item.Version == "6.1.0");
        Check(toolManagementView.ToggleButton.IsEnabled && Equals(toolManagementView.ToggleButton.Content, "禁用此版本") && toolManagementView.DebugCheckButton.IsEnabled,
            "enabled version exposes disable and debug checks");
        toolManagementView.SetBusy(true);
        Check(!toolManagementView.ToggleButton.IsEnabled && !toolManagementView.DebugCheckButton.IsEnabled, "busy manager prevents concurrent component mutations and debug checks");
        toolManagementView.SetBusy(false);
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            success = true,
            hardware = false,
            network = false,
            checks
        });
        async Task Layout()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
    }
}
