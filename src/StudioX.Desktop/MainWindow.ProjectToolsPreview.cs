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
        void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); checks.Add(text); }
        await ShowFirstProjectAsync(); await RunGuideActionAsync("tools");
        Check(projectToolsTab is not null && WorkspaceTabs.SelectedItem == projectToolsTab && projectToolsView!.Plan?.Requirements.Count == 0,
            "first guide opens preparation without guessing a compiler");
        Check(workbenchCommands.Any(c => c.Title == "准备工程工具…"), "tools menu and command palette include preparation");
        await ShowProjectToolsAsync(project);
        Check(projectToolsView!.Selected is { State: ProjectToolState.Missing } && !projectToolsView.CanDownload, "missing pinned tool is visible before any catalog request");
        projectToolsView.Source = catalog;
        await RunProjectToolsAsync("load");
        Check(projectToolsView.CanDownload && projectToolsView.DetailText.Contains("许可证") && projectToolsView.DetailText.Contains("SHA-256") && projectToolsView.DetailText.Contains("磁盘"),
            "explicit offline catalog load displays exact version, source, hash, size and disk budget");
        projectToolsView.SetBusy(true); Check(!projectToolsView.CanDownload, "busy preparation disables duplicate download"); projectToolsView.SetBusy(false);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); Width = 1440; Height = 960; await Layout();
            Check(projectToolsView.HasUsableSpace, theme.Id + " keeps requirement table and detail readable");
            Render(this, Path.Combine(output, "project-tools-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark); Width = MinWidth; Height = 720; await Layout();
        Check(projectToolsView.HasUsableSpace && projectToolsView.PrimaryActionsVisible, "compact window keeps primary actions, requirement table and details accessible");
        Render(this, Path.Combine(output, "project-tools-compact.png"));
        await CloseWorkspaceTabAsync(projectToolsTab!); await ShowProjectToolsAsync(project);
        Check(projectToolsTab!.Visibility == Visibility.Visible && projectToolsView.Listing is not null && projectToolsView.CanDownload, "reopening tab preserves explicitly loaded catalog");
        projectToolsView.Source = catalog + ".changed";
        Check(projectToolsView.Listing is null && !projectToolsView.CanDownload, "editing source invalidates old catalog download selection");
        await ShowProjectToolsAsync(Path.Combine(output, "missing-project"));
        Check(projectToolsView.Plan is null && projectToolsView.Selected is null && !projectToolsView.CanDownload && projectToolsView.DetailText.Contains("Exception", StringComparison.Ordinal),
            "failed inspection clears stale plan and retains original diagnostic");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { success = true, hardware = false, network = false, checks });
        async Task Layout() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); UpdateLayout(); }
    }
}
