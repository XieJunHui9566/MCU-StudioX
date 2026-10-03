namespace StudioX.Desktop;

using System.Text.Json;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        Check(workbenchCommands.Any(command => command.Title == "开发环境组件管理…"), "tools menu and palette expose management command");
        Check(workbenchCommands.Any(command => command.Title == "导入开发环境组件…") && Equals(toolManagementView.InstallButton.Content, "导入开发环境组件…"), "menu and version manager share development component import entry");
        Check(report.Versions.Where(version => version.Latest).All(version => !version.CanRetire), "latest installed versions have no retirement action");
        Check(DevelopmentComponentsMenu.Items.OfType<MenuItem>().Count() == 5 && ToolsRootMenu.Items.OfType<MenuItem>().Count() <= 7,
            "related actions are grouped in a compact development component submenu");
        Check(ToolsRootMenu.Items.OfType<MenuItem>().All(item => !Equals(item.Header, "开发环境组件管理…"))
            && DevelopmentComponentsMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "开发环境组件管理…")), "management has a single grouped entry rather than duplicate environment pages");
        Check(workbenchCommands.Single(command => command.Title == "校验当前工程的开发环境组件").Enabled() == false, "project verification is unavailable without a current project");
        Check(ToolInventory.Text.Contains("使用此组件时校验") && !ToolInventory.Text.Contains("完整性与启动检查通过"), "startup inventory displays metadata without full hashing or version execution");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); Width = 1440; Height = 960;
            await SettleAsync();
            Check(toolManagementView.VersionsGrid.ActualHeight > 100 && toolManagementView.DetailText.ActualHeight > 100, theme.Id + " keeps versions and dependency details readable");
            Render(this, Path.Combine(directory, "tools-" + theme.Id + ".png"));
        }
        var wallpaper = new DrawingVisual();
        using (var context = wallpaper.RenderOpen())
            context.DrawRectangle(new LinearGradientBrush(Color.FromRgb(23, 110, 168), Color.FromRgb(154, 61, 132), 25), null, new Rect(0, 0, 1440, 960));
        var bitmap = new RenderTargetBitmap(1440, 960, 96, 96, PixelFormats.Pbgra32); bitmap.Render(wallpaper);
        var wallpaperPath = Path.Combine(directory, "wallpaper-fixture.png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(wallpaperPath)) encoder.Save(stream);
        var asset = await services.Appearance.ImportAsync(wallpaperPath, BackgroundKind.Image);
        ApplyTheme(ThemeService.Dark); ApplyBackground(new(Kind: BackgroundKind.Image, Asset: asset, Opacity: 1, Dim: 0.12));
        await SettleAsync();
        Check(toolManagementView.Background is SolidColorBrush { Color.A: 0 }
            && toolManagementView.DetailText.Background is SolidColorBrush { Color.A: < 255 }, "wallpaper remains visible through the management page and themed detail surface");
        Render(this, Path.Combine(directory, "tools-wallpaper.png"));
        DevelopmentComponentsMenu.IsSubmenuOpen = true;
        await SettleAsync();
        Check(DevelopmentComponentsMenu.Items.OfType<MenuItem>().All(item => item.Header.ToString()!.Contains("开发环境组件")), "component menu terminology is consistent");
        DevelopmentComponentsMenu.IsSubmenuOpen = false;
        var originalTab = toolManagementTab!;
        await CloseWorkspaceTabAsync(originalTab);
        await ShowToolManagementAsync(project);
        Check(toolManagementTab == originalTab && originalTab.Visibility == System.Windows.Visibility.Visible, "closed manager tab reopens without duplication");
        var fixture = new ManagedToolVersion("fixture.gcc", "1.0.0", "UI fixture", "fixture", 1024, 2, true, null, false, false, true, "fixture", "fixture", [], "fixture", "UI fixture only");
        toolManagementView.SetReport(new(DateTimeOffset.UtcNow, [fixture], [], [], true));
        Check(toolManagementView.RetireButton.IsEnabled && !toolManagementView.RestoreButton.IsEnabled && toolManagementView.PurgeButton.IsEnabled && toolManagementView.ToggleButton.IsEnabled,
            "disable and delete are prominently available for selected installed versions");
        toolManagementView.SetReport(new(DateTimeOffset.UtcNow, [fixture with { References = ["closed project"] }], [], [], true));
        Check(toolManagementView.RetireButton.IsEnabled && toolManagementView.PurgeButton.IsEnabled && toolManagementView.ManagementHint.Text.Contains("引用"), "referenced version exposes explicit removal with a visible dependency impact");
        toolManagementView.SetReport(new(DateTimeOffset.UtcNow, [fixture with { Busy = true }], [], [], true));
        Check(!toolManagementView.RetireButton.IsEnabled && !toolManagementView.PurgeButton.IsEnabled && !toolManagementView.ToggleButton.IsEnabled
            && toolManagementView.ManagementHint.Text.Contains("正在使用"), "occupied version disables mutations and explains how to release it");
        toolManagementView.SetReport(new(DateTimeOffset.UtcNow, [fixture with { Latest = true, Enabled = false }], [], [], true));
        Check(toolManagementView.ToggleButton.IsEnabled && Equals(toolManagementView.ToggleButton.Content, "启用") && toolManagementView.PurgeButton.IsEnabled,
            "latest and disabled identities remain manageable without silently selecting another version");
        toolManagementView.SetReport(new(DateTimeOffset.UtcNow, [fixture with { SafeToManage = false }], [], ["fixture failure"], false));
        Check(!toolManagementView.PurgeButton.IsEnabled && !toolManagementView.ToggleButton.IsEnabled && toolManagementView.ManagementHint.Text.Contains("未完成"), "incomplete dependencies explain why management is blocked");
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
