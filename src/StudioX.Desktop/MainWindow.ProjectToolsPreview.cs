namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Tools;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>独立用户目录和离线测试目录；只检查入口与布局，不联网或执行工具。</summary>
    public async Task RenderProjectToolsPreviewAsync(string output, string project, string catalog)
    {
        var checks = new List<string>();
        void Check(bool value, string text)
        {
            if (!value)
            {
                throw new InvalidOperationException(text);
            }
            checks.Add(text);
        }
        await ShowFirstProjectAsync();
        await RunGuideActionAsync("tools");
        Check(projectToolsTab is not null && WorkspaceTabs.SelectedItem == projectToolsTab && projectToolsView!.Plan?.Requirements.Count == 0,
            "first guide opens preparation without guessing a compiler");
        Check(projectToolsView!.HasTrustedCatalogAction && projectToolsView.Listing is null,
            "trusted catalog and upgrade actions are available without automatic catalog requests");
        Check(projectToolsView.HasAcquisitionModes && !projectToolsView.CanAcquire && !projectToolsView.CanImport,
            "two acquisition modes are visible without guessing project requirements");
        await ShowDistributionAsync();
        Check(distributionView!.HasTrustedCatalogAction && distributionView.HasMigrationAction && distributionView.Listing is null && !distributionView.CanInstall,
            "distribution exposes verified catalog action without loading or enabling installation");
        var offlineListing = await services.Distribution.ReadAsync(catalog);
        distributionView.SetListing(offlineListing);
        distributionView.SelectFirst();
        Check(distributionView.CanInstall, "explicit offline catalog can be selected for installation");
        distributionView.Source = catalog + ".changed";
        Check(distributionView.Listing is null && !distributionView.CanInstall, "editing distribution source invalidates old signature and download choices");
        distributionView.SetListing(offlineListing);
        distributionView.SelectFirst();
        await RunDistributionAsync("clear-key");
        Check(distributionView.Listing is null && !distributionView.CanInstall, "changing distribution trust invalidates old listing");
        Check(workbenchCommands.Any(c => c.Title == "准备工程开发环境组件…"), "tools menu and command palette include preparation");
        Check(workbenchCommands.Any(c => c.Title == "导入开发环境组件…"), "full and light builds expose the shared development component import command");
        Check(workbenchCommands.Any(c => c.Title == "从 GitHub 获取开发环境组件…"), "tools menu exposes the built-in GitHub component library");
        Check(DevelopmentComponentDialogs.ImportFilter.Contains("*.mcutoolchain") && DevelopmentComponentDialogs.ImportFilter.Contains("*.studioxtools")
            && DevelopmentComponentDialogs.ExportFilter.StartsWith("MCU 开发环境组件组件|*.mcutoolchain"), "import accepts canonical and existing archives while export defaults to mcutoolchain");
        await ShowProjectToolsAsync(project);
        Check(projectToolsView!.Selected is { State: ProjectToolState.Missing } && !projectToolsView.CanDownload, "missing pinned tool is visible before any catalog request");
        Check(projectToolsView.CanAcquire && projectToolsView.CanImport && projectToolsView.Listing is null,
            "automatic acquisition and manual import need no catalog address or prior network request");
        projectToolsView.SelectManualMode(true);
        await Layout();
        Check(projectToolsView.CanImport && projectToolsView.Listing is null, "selecting manual mode does not load a catalog");
        Render(this, Path.Combine(output, "project-tools-manual.png"));
        projectToolsView.SelectManualMode(false);
        projectToolsView.Source = catalog;
        await RunProjectToolsAsync("load");
        Check(projectToolsView.CanDownload && projectToolsView.DetailText.Contains("许可证") && projectToolsView.DetailText.Contains("SHA-256") && projectToolsView.DetailText.Contains("磁盘"),
            "explicit offline catalog load displays exact version, source, hash, size and disk budget");
        await ShowDistributionAsync();
        distributionView.SetListing(offlineListing);
        distributionView.SelectFirst();
        await RunDistributionAsync("preview-tool");
        Check(distributionView.DetailText.Contains("没有选择工程") && distributionView.DetailText.Contains("保留工程源码") && distributionView.CanInstall,
            "explicit upgrade preview downloads only selected fixture and clearly reports no current project without installing it");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Width = 1440;
            Height = 960;
            await Layout();
            Check(distributionView.ListHasSpace, theme.Id + " keeps component upgrade details readable");
            Render(this, Path.Combine(output, "component-upgrade-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        Width = MinWidth;
        Height = 720;
        await Layout();
        Check(distributionView.ListHasSpace && distributionView.PrimaryActionsVisible, "compact distribution keeps upgrade actions, summary and list accessible");
        Render(this, Path.Combine(output, "component-upgrade-compact.png"));
        ShowDocument(projectToolsTab!);
        projectToolsView.SetBusy(true);
        Check(!projectToolsView.CanDownload && !projectToolsView.CanAcquire && !projectToolsView.CanImport,
            "busy preparation disables duplicate acquisition and import");
        projectToolsView.SetBusy(false);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Width = 1440;
            Height = 960;
            await Layout();
            Check(projectToolsView.HasUsableSpace, theme.Id + " keeps requirement table and detail readable");
            Render(this, Path.Combine(output, "project-tools-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        Width = MinWidth;
        Height = 720;
        await Layout();
        Check(projectToolsView.HasUsableSpace && projectToolsView.PrimaryActionsVisible, "compact window keeps primary actions, requirement table and details accessible");
        Render(this, Path.Combine(output, "project-tools-compact.png"));
        await CloseWorkspaceTabAsync(projectToolsTab!);
        await ShowProjectToolsAsync(project);
        Check(projectToolsTab!.Visibility == Visibility.Visible && projectToolsView.Listing is not null && projectToolsView.CanDownload, "reopening tab preserves explicitly loaded catalog");
        projectToolsView.Source = catalog + ".changed";
        Check(projectToolsView.Listing is null && !projectToolsView.CanDownload, "editing source invalidates old catalog download selection");
        await ShowProjectToolsAsync(Path.Combine(output, "missing-project"));
        Check(projectToolsView.Plan is null && projectToolsView.Selected is null && !projectToolsView.CanDownload && projectToolsView.DetailText.Contains("Exception", StringComparison.Ordinal),
            "failed inspection clears stale plan and retains original diagnostic");
        await ShowToolManagementAsync(project);
        Check(toolManagementView!.InstallButton.IsEnabled && Equals(toolManagementView.InstallButton.Content, "导入开发环境组件…"),
            "development component import stays available without preinstalled firmware toolchains");
        Width = 1440;
        Height = 960;
        await Layout();
        Render(this, Path.Combine(output, "development-components-import.png"));
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            success = true,
            hardware = false,
            network = false,
            checks
        });
        async Task Layout()
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
    }
}
