namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>以真实扫描结果检查配置向导；不保存工程配置、不启动编译或设备会话。</summary>
    public async Task RenderLvglSetupPreviewAsync(string directory, string project, string librarySearchDirectory, string uiDirectory)
    {
        await OpenProjectAsync(project, CancellationToken.None);
        await ShowLvglPreviewAsync();
        await LvglPreview.InspectSetupForPreviewAsync(librarySearchDirectory, uiDirectory);
        if (LvglPreview.SetupCandidateCount == 0 || LvglPreview.SetupSourceCount == 0)
        {
            throw new InvalidOperationException("实际工程扫描未得到 LVGL 库或 UI 源码，不能用空页面通过布局验证。");
        }

        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Width = 1460;
            Height = 980;
            foreach (var step in new[] { 0, 1, 2 })
            {
                LvglPreview.ShowSetupStepForPreview(step);
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Render(this, Path.Combine(directory, $"lvgl-setup-{step + 1}-{theme.Id}.png"));
            }
        }
        ApplyTheme(ThemeService.Dark);
        Width = 1100;
        Height = 760;
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "lvgl-setup-narrow.png"));
        await CloseProjectAsync(CancellationToken.None);
        if (LvglPreview.ConfigurationReady || LvglPreview.SetupCandidateCount != 0 || LvglPreview.SetupSourceCount != 0)
        {
            throw new InvalidOperationException("关闭工程后仍遗留预览配置或向导扫描结果。");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: real LVGL library and shared UI scanned through application services; dark/light/1100px setup layouts rendered; project close clears setup binding. No project files modified, native preview process or hardware started.\n");
    }
}
