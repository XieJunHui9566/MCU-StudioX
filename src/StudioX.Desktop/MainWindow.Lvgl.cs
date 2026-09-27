namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    private void InitializeLvglPreview()
    {
        LvglPreview.Attach(services.LvglPreview);
        LvglPreview.LogDiagnostic = Log;
        LvglPreview.BeforeStartAsync = async (directory, token) =>
        {
            if (!string.Equals(directory, projectDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            await SaveAllSourcesAsync(directory, token);
        };
    }

    private void LvglPreview_Click(object sender, RoutedEventArgs e)
    {
        LvglPreview.SetProject(projectDirectory);
        ShowDocument(LvglPreviewTab);
    }

    public async Task ShowLvglPreviewAsync(bool start = false)
    {
        LvglPreview_Click(this, new RoutedEventArgs());
        await LvglPreview.WaitForProjectAsync();
        if (start)
        {
            await LvglPreview.StartPreviewAsync();
        }
    }

    /// <summary>验证页面布局和工程绑定，只读取现有工程，不启动硬件或 PC 进程。</summary>
    public async Task RenderLvglPreviewAsync(string directory, string project)
    {
        await OpenProjectAsync(project, CancellationToken.None);
        await ShowLvglPreviewAsync();
        if (!LvglPreview.ConfigurationReady)
        {
            throw new InvalidOperationException("实际 LVGL 工程的预览配置未加载。");
        }
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            Width = 1460;
            Height = 920;
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "lvgl-preview-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        Width = 1100;
        Height = 760;
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "lvgl-preview-narrow.png"));
        await CloseProjectAsync(CancellationToken.None);
        if (LvglPreview.ConfigurationReady)
        {
            throw new InvalidOperationException("关闭工程后仍遗留预览配置。");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: actual LVGL project config and target memory loaded; dark/light/1100px layouts rendered; project close clears preview binding. No native process or hardware started.\n");
    }
}
