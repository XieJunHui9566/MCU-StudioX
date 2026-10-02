namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Tools;

public partial class MainWindow
{
    /// <summary>实际工具目录只读；按钮状态使用独立数据夹具，不安装或删除本机工具。</summary>
    public async Task RenderToolManagementPreviewAsync(string directory, string project)
    {
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        await ShowToolManagementAsync(project);
        var report = toolManagementView!.Report!;
        Check(report.ReferencesComplete && report.Versions.Count > 0, "real installed toolsets expose complete space and dependency report");
        Check(report.Projects.Contains(project), "selected health project is included even when editor has no open project");
        Check(report.Versions.Any(version => version.Id == "espressif.idf" && version.References.Any(reference => reference.Contains(project))), "real ESP-IDF project reserves its exact SDK version");
        Check(workbenchCommands.Any(command => command.Title == "工具占用与升级管理…"), "tools menu and palette expose management command");
        Check(report.Versions.Where(version => version.Latest).All(version => !version.CanRetire), "latest installed versions have no retirement action");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); Width = 1440; Height = 960;
            await SettleAsync();
            Check(toolManagementView.VersionsGrid.ActualHeight > 100 && toolManagementView.DetailText.ActualHeight > 100, theme.Id + " keeps versions and dependency details readable");
            Render(this, Path.Combine(directory, "tools-" + theme.Id + ".png"));
        }
        var originalTab = toolManagementTab!;
        await CloseWorkspaceTabAsync(originalTab);
        await ShowToolManagementAsync(project);
        Check(toolManagementTab == originalTab && originalTab.Visibility == System.Windows.Visibility.Visible, "closed manager tab reopens without duplication");
        var fixture = new ManagedToolVersion("fixture.gcc", "1.0.0", "UI fixture", "fixture", 1024, 2, true, null, false, false, true, "fixture", "fixture", [], "fixture", "UI fixture only");
        toolManagementView.SetReport(new(DateTimeOffset.UtcNow, [fixture], [], [], true));
        Check(toolManagementView.RetireButton.IsEnabled && !toolManagementView.RestoreButton.IsEnabled && !toolManagementView.PurgeButton.IsEnabled, "unused installed older version exposes only reversible cleanup");
        toolManagementView.SetReport(new(DateTimeOffset.UtcNow, [fixture with { References = ["closed project"] }], [], [], true));
        Check(!toolManagementView.RetireButton.IsEnabled, "referenced old version cannot be selected for cleanup");
        toolManagementView.SetReport(new(DateTimeOffset.UtcNow, [fixture with { Busy = true }], [], [], true));
        Check(!toolManagementView.RetireButton.IsEnabled, "occupied old version disables cleanup");
        toolManagementView.SetReport(new(DateTimeOffset.UtcNow, [fixture with { Installed = false, RetirementId = Guid.NewGuid().ToString("N") }], [], [], true));
        Check(toolManagementView.RestoreButton.IsEnabled && toolManagementView.PurgeButton.IsEnabled && !toolManagementView.RetireButton.IsEnabled, "recoverable version exposes separate restore and permanent deletion");
        toolManagementView.SetBusy(true, "正在检查…");
        Check(!toolManagementView.Toolbar.IsEnabled && !toolManagementView.RestoreButton.IsEnabled && !toolManagementView.PurgeButton.IsEnabled, "busy manager prevents duplicate mutations");
        toolManagementView.SetBusy(false);
        await toolManagementView.Requested!("help");
        Check(helpCenter?.SelectedArticle?.Id == "tool-environment", "management guide opens the actual bundled help topic");
        await ShowToolManagementAsync(project);
        ApplyTheme(ThemeService.Dark); Width = MinWidth; Height = 720;
        await SettleAsync();
        Render(this, Path.Combine(directory, "tools-compact.png"));
        Check(toolManagementView.VersionsGrid.ActualHeight > 55 && toolManagementView.DetailText.ActualHeight > 55,
            $"compact manager keeps both result and detail scroll areas ({toolManagementView.VersionsGrid.ActualHeight:F0}/{toolManagementView.DetailText.ActualHeight:F0})");
        await File.WriteAllTextAsync(Path.Combine(directory, "tool-management-ui-result.json"), JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
        async Task SettleAsync() { UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); UpdateLayout(); }
    }
}
