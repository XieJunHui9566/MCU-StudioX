namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;
using StudioX.Engine;

public partial class MainWindow
{
    private ProjectManifest? currentProjectManifest;
    private int projectDetailsRevision;

    private async void DevicesAndTemplates_Click(object sender, RoutedEventArgs e)
    {
        if (projectDirectory is null) await RunAsync(token => BeginNewProjectAsync(token));
        else await RunAsync(_ => ShowProjectDetailsAsync());
    }

    private void SetProjectDetailsMode(ProjectManifest? project)
    {
        currentProjectManifest = project;
        projectDetailsRevision++;
        NewProjectPanel.Visibility = project is null ? Visibility.Visible : Visibility.Collapsed;
        NewProjectPanel.IsEnabled = project is null;
        ProjectDetailsPanel.Visibility = project is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectDetailsPanel.DataContext = null;
        loadedBuildSettings = null;
        stcCodeRomLimit = null;
        ConfigureBuildSettingsForProject();
        UpdateBuildSettingsControls();
        ConfigureStcIspForProject();
        RefreshAg32LogicUi(project);
    }

    private async Task ShowProjectDetailsAsync()
    {
        if (projectDirectory is not { } directory || currentProjectManifest is not { } project) return;
        var revision = ++projectDetailsRevision;
        NewProjectPanel.Visibility = Visibility.Collapsed;
        NewProjectPanel.IsEnabled = false;
        ProjectDetailsPanel.Visibility = Visibility.Visible;
        CurrentProjectName.Text = project.Name;
        CurrentProjectDeviceId.Text = project.DeviceId;
        CurrentProjectTemplateId.Text = project.Kind == ProjectKind.CubeMx ? "CubeMX 导入" : project.TemplateId;
        CurrentProjectDescriptionHeading.Text = project.Kind == ProjectKind.CubeMx ? "工程来源说明" : "模板默认配置（创建时）";
        TemplateDefaultsNote.Visibility = project.Kind == ProjectKind.CubeMx ? Visibility.Collapsed : Visibility.Visible;
        CurrentProjectPackVersion.Text = project.Kind == ProjectKind.CubeMx ? "不适用" : project.PackVersion;
        CurrentProjectToolset.Text = $"{project.ToolsetId} {project.ToolsetVersion} · {project.CompilerId}";
        ApplyProjectDetails(ProjectDeviceInfo.Recorded(project));
        loadedBuildSettings = null;
        stcCodeRomLimit = null;
        BuildSettingsStatus.Text = "正在读取编译参数…";
        UpdateBuildSettingsControls();
        ShowDocument(PackagesTab);
        var details = await ProjectDeviceInfo.ReadAsync(directory, project);
        var settings = await services.Builds.LoadSettingsAsync(directory);
        var romLimit = await services.Builds.ReadStcCodeRomLimitAsync(directory);
        // 工程切换、关闭或窗口关闭期间完成的旧读取不能覆盖新页面。
        if (revision == projectDetailsRevision && projectDirectory == directory && !closing)
        {
            stcCodeRomLimit = romLimit; ApplyProjectDetails(details); ApplyBuildSettings(settings);
            if (IsStcSdccProject) await LoadStcIspProjectAsync(directory, revision);
        }
    }

    private void ApplyProjectDetails(ProjectDeviceInfo details)
    {
        ProjectDetailsPanel.DataContext = details;
        var manufacturer = ManufacturerOption.FromId(details.Manufacturer);
        CurrentProjectVendor.Text = manufacturer.Description;
        CurrentProjectVendorLogo.Source = manufacturer.Logo;
        CurrentProjectVendorMonogram.Text = manufacturer.Monogram;
        CurrentProjectVendorMonogram.Visibility = manufacturer.Logo is null ? Visibility.Visible : Visibility.Collapsed;
        CurrentProjectVendorLogoFrame.Visibility = Visibility.Visible;
    }
}
