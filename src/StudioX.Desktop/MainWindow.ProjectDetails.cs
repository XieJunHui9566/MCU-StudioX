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
        if (projectDirectory is null)
        {
            await RunAsync(token => BeginNewProjectAsync(token));
        }
        else
        {
            await RunAsync(_ => ShowProjectDetailsAsync());
        }
    }

    private void SetProjectDetailsMode(ProjectManifest? project)
    {
        currentProjectManifest = project;
        projectDetailsRevision++;
        ZephyrDeviceTreeButton.Visibility = project?.Kind == ProjectKind.Zephyr ? Visibility.Visible : Visibility.Collapsed;
        ZephyrDeviceTreeButton.IsEnabled = false;
        NewProjectPanel.Visibility = project is null ? Visibility.Visible : Visibility.Collapsed;
        NewProjectPanel.IsEnabled = project is null;
        ProjectDetailsPanel.Visibility = project is null ? Visibility.Collapsed : Visibility.Visible;
        ProjectDetailsPanel.DataContext = null;
        BuildSettingsCard.Visibility = project?.Kind is ProjectKind.Zephyr or ProjectKind.MicroPython ? Visibility.Collapsed : Visibility.Visible;
        loadedBuildSettings = null;
        stcCodeRomLimit = null;
        ConfigureBuildSettingsForProject();
        UpdateBuildSettingsControls();
        ConfigureEspressifModuleForProject();
        ConfigureStcIspForProject();
        RefreshAg32LogicUi(project);
    }

    private async Task ShowProjectDetailsAsync()
    {
        if (projectDirectory is not { } directory || currentProjectManifest is not { } project)
        {
            return;
        }
        var revision = ++projectDetailsRevision;
        NewProjectPanel.Visibility = Visibility.Collapsed;
        NewProjectPanel.IsEnabled = false;
        ProjectDetailsPanel.Visibility = Visibility.Visible;
        CurrentProjectName.Text = project.Name;
        CurrentProjectDeviceId.Text = project.Kind == ProjectKind.Zephyr
            ? project.Zephyr!.BoardTarget : project.DeviceId;
        CurrentProjectTemplateId.Text = project.Kind == ProjectKind.CubeMx ? "CubeMX 导入" : project.TemplateId;
        CurrentProjectDescriptionHeading.Text = project.Kind switch
        {
            ProjectKind.CubeMx => "工程来源说明",
            ProjectKind.Zephyr => "Zephyr 实验板级配置（创建时）",
            _ => "模板默认配置（创建时）"
        };
        TemplateDefaultsNote.Visibility = project.Kind is ProjectKind.CubeMx or ProjectKind.Zephyr or ProjectKind.MicroPython
            ? Visibility.Collapsed : Visibility.Visible;
        CurrentProjectPackVersion.Text = project.Kind == ProjectKind.CubeMx ? "不适用" : project.PackVersion;
        CurrentProjectToolset.Text = project.Kind == ProjectKind.Zephyr
            ? $"Zephyr {project.Zephyr!.ZephyrVersion} · 实验模式 · {project.Zephyr.BoardTarget}"
            : $"{project.ToolsetId} {project.ToolsetVersion} · {project.CompilerId}";
        ApplyProjectDetails(ProjectDeviceInfo.Recorded(project));
        loadedBuildSettings = null;
        stcCodeRomLimit = null;
        BuildSettingsStatus.Text = project.Kind == ProjectKind.Zephyr
            ? "Zephyr 实验模式由 Kconfig、Devicetree 与 west 管理；当前未启用板级构建。"
            : "正在读取编译参数…";
        UpdateBuildSettingsControls();
        ConfigureEspressifModuleForProject();
        ShowDocument(PackagesTab);
        var details = await ProjectDeviceInfo.ReadAsync(directory, project);
        if (project.Kind == ProjectKind.MicroPython)
        {
            CurrentProjectToolset.Text = $"MicroPython {project.MicroPython!.Version} · {project.MicroPython.Board}";
            BuildSettingsStatus.Text = "MicroPython 脚本不使用 GCC 编译参数；请通过 MicroPython 页面上传。";
            if (revision == projectDetailsRevision && projectDirectory == directory && !closing)
            {
                ApplyProjectDetails(details);
            }
            return;
        }
        if (project.Kind == ProjectKind.Zephyr)
        {
            if (revision == projectDetailsRevision && projectDirectory == directory && !closing)
            {
                ApplyProjectDetails(details);
            }
            return;
        }
        var settings = await services.Builds.LoadSettingsAsync(directory);
        var romLimit = await services.Builds.ReadStcCodeRomLimitAsync(directory);
        // 工程切换、关闭或窗口关闭期间完成的旧读取不能覆盖新页面。
        if (revision == projectDetailsRevision && projectDirectory == directory && !closing)
        {
            stcCodeRomLimit = romLimit;
            ApplyProjectDetails(details);
            ApplyBuildSettings(settings);
            if (IsEspressifProject)
            {
                await LoadEspressifModuleAsync(directory, revision);
            }
            if (IsStcSdccProject)
            {
                await LoadStcIspProjectAsync(directory, revision);
            }
        }
    }

    private void ApplyProjectDetails(ProjectDeviceInfo details)
    {
        ProjectDetailsPanel.DataContext = details;
        var manufacturer = ManufacturerOption.FromId(details.Manufacturer);
        CurrentProjectVendor.Text = manufacturer.Description;
        CurrentProjectVendorLogo.Source = manufacturer.Logo;
        CurrentProjectVendorLogoFrame.Width = Math.Max(56, manufacturer.LogoFrameWidth);
        CurrentProjectVendorMonogram.Text = manufacturer.Monogram;
        CurrentProjectVendorMonogram.Visibility = manufacturer.Logo is null ? Visibility.Visible : Visibility.Collapsed;
        CurrentProjectVendorLogoFrame.Visibility = Visibility.Visible;
    }
}
