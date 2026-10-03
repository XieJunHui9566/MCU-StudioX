namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Health;
using StudioX.Engine;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>在隔离用户目录检查健康页；指定工程只读取，故障与缓存修复使用新夹具。</summary>
    public async Task RenderProjectHealthPreviewAsync(string directory, string project)
    {
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        healthDirectory = project;
        await ShowProjectHealthAsync();
        Check(projectHealthView?.Report is { Errors: 0, NativeBuildApplicable: true }, "real project displays complete read-only preflight");
        Check(projectDirectory is null, "health page can inspect a project that is not open in the editor");
        Check(workbenchCommands.Count(command => command.Title == "工程健康检查…") == 2, "tools and help menus expose the health command");
        var originalTab = projectHealthTab!;
        await CloseWorkspaceTabAsync(originalTab);
        await ShowProjectHealthAsync();
        Check(projectHealthTab == originalTab && originalTab.Visibility == Visibility.Visible, "closed health tab reopens without duplication");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Width = 1440;
            Height = 960;
            await SettleAsync();
            Check(projectHealthView!.ChecksGrid.ActualHeight > 100 && projectHealthView.DetailText.ActualHeight > 100, theme.Id + " exposes scrolling results and diagnosis");
            Render(this, Path.Combine(directory, "health-" + theme.Id + ".png"));
        }
        var real = projectHealthView!.Report!;
        projectHealthView.ChecksGrid.SelectedItem = real.Checks.First(check => check.Code == "HEALTH_SDK");
        Check(!projectHealthView.ActionButton.IsEnabled && projectHealthView.HelpButton.IsEnabled, "settings action requires the same active project while help stays available");
        await projectHealthView.HelpRequested!(projectHealthView.SelectedCheck!.HelpTopic);
        Check(helpCenter?.SelectedArticle?.Id == "esp-idf", "selected diagnostic opens the corresponding actual help article");
        await ShowProjectHealthAsync(diagnostic: "cc1.exe: XTENSA_GNU_CONFIG pointed different files");
        Check(projectHealthView.Report!.Checks.Any(check => check.Code == "HEALTH_PREVIOUS_DIAGNOSTIC" && check.RawDiagnostic.Contains("XTENSA_GNU_CONFIG")), "failure route retains original tool diagnostics in the health report");
        projectHealthView.SetBusy(true, "正在检查…");
        Check(!projectHealthView.ActionButton.IsEnabled && !projectHealthView.Toolbar.IsEnabled, "busy inspection prevents repair and export actions");
        projectHealthView.BeginInspection();
        projectHealthView.SetBusy(false, "已取消");
        Check(projectHealthView.Report is null && !projectHealthView.ExportButton.IsEnabled && !projectHealthView.ActionButton.IsEnabled, "cancelled inspection cannot use stale report actions");
        var fixture = Path.Combine(directory, "failed-project");
        await JsonStore.WriteAsync(Path.Combine(fixture, ".studiox", "project.json"), new ProjectManifest(1, "offline", "validation.pack", "1.0.0", "offline",
            "OfflineDevice", "minimal", "validation.missing", "1.0.0", "offline"));
        var failed = await BuildWithSummaryAsync(fixture, CancellationToken.None);
        Check(!failed.Success && failed.Artifacts.Count == 0 && !Directory.Exists(Path.Combine(fixture, ".build")), "automatic build preflight blocks errors before the build backend runs");
        Check(projectHealthView.Report!.Checks.Any(check => check.Code == "TOOLSET_MISSING"), "automatic failure displays the missing locked toolset");
        projectHealthView.ChecksGrid.SelectedItem = projectHealthView.Report.Checks.First(check => check.Code == "TOOLSET_MISSING");
        Check(projectHealthView.ActionButton.IsEnabled && projectHealthView.ActionButton.Content.ToString() == "打开对应开发环境组件", "missing toolset exposes its repair entry");
        Check(projectHealthView.DetailText.Text.Contains("原始诊断") && projectHealthView.DetailText.Text.Contains("TOOLSET_MISSING"), "selected failure displays the full original exception");
        ApplyTheme(ThemeService.Dark);
        Width = 1440;
        Height = 960;
        await SettleAsync();
        Render(this, Path.Combine(directory, "health-failure.png"));
        ShowTroubleshooting("HEALTH_CACHE_PROJECT CMAKE_HOME_DIRECTORY=old-path");
        Check(TroubleshootingService.Explain(lastFailure).Action == "health" && troubleshootingTab!.Content is DockPanel panel
            && panel.Children.OfType<StackPanel>().Single().Children.OfType<Button>().Any(button => button.Content?.ToString() == "检查工程配置缓存"), "cache failure guide contains a concrete health repair button");
        await ShowProjectHealthAsync();
        Width = MinWidth;
        Height = 720;
        await SettleAsync();
        Check(projectHealthView.ChecksGrid.ActualHeight > 55 && projectHealthView.DetailText.ActualHeight > 55, "compact window retains both result and original diagnosis scroll areas");
        Render(this, Path.Combine(directory, "health-compact.png"));
        projectHealthView.Invalidate();
        Check(projectHealthView.Report is null && !projectHealthView.ExportButton.IsEnabled, "project switch invalidates old inspection and export state");
        await File.WriteAllTextAsync(Path.Combine(directory, "health-ui-result.json"), JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
        async Task SettleAsync()
        {
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
        }
    }
}
