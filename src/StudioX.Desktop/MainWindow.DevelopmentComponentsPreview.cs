namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Tools;
using StudioX.Foundation;

public partial class MainWindow
{
    /// <summary>隔离用户数据，仅测试需求显示与缺失入口；不执行工具或访问硬件。</summary>
    public async Task RenderDevelopmentComponentsPreviewAsync(string output, string archive, string project)
    {
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        var pack = await services.Packs.ImportAsync(archive);
        await BeginNewProjectAsync(CancellationToken.None);
        var selected = installedPacks.Single(p => p.Manifest.Id == pack.Manifest.Id);
        SelectPack(selected);
        var device = selected.Manifest.Devices.Single();
        DevicePicker.SelectedItem = device;
        TemplatePicker.SelectedItem = device.Templates.Single();
        Check(SelectedDevelopmentComponents.Text.Contains("test.gcc 1.0.0")
            && SelectedDevelopmentComponents.Text.Contains("test.mapper 1.0.0") && CreateProjectButton.IsEnabled,
            "creation shows both exact requirements and remains enabled without tools");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); Width = 1440; Height = 960;
            SelectedDevelopmentComponents.BringIntoView(); await Layout();
            Check(SelectedDevelopmentComponents.IsVisible && SelectedDevelopmentComponents.ActualWidth > 160,
                theme.Id + " displays readable selected component requirements");
            Render(this, Path.Combine(output, "create-components-" + theme.Id + ".png"));
        }
        await ShowProjectToolsAsync(project);
        Check(projectToolsView!.Plan!.Requirements is { Count: 2 } needs && needs.All(r => r.State == ProjectToolState.Missing),
            "empty-runtime preparation displays every declared component separately");
        ApplyTheme(ThemeService.Dark); Width = MinWidth; Height = 720; await Layout();
        Check(projectToolsView.HasUsableSpace && projectToolsView.PrimaryActionsVisible,
            "compact preparation keeps multi-component requirements and import actions accessible");
        Render(this, Path.Combine(output, "prepare-components-compact.png"));
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { success = true, hardware = false, network = false, checks });
        async Task Layout() { await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); UpdateLayout(); }
    }
}
