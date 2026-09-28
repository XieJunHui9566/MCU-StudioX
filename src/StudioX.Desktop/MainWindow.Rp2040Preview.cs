namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>用隔离数据验证 RP2040 C 包的选择、编辑及硬件入口边界。</summary>
    public async Task RenderRp2040PreviewAsync(string directory, string archive)
    {
        var pack = await ImportPackForSelectionAsync(archive, CancellationToken.None);
        ShowDocument(PackagesTab);
        SelectPack(pack);
        DeviceSearch.Text = "RP2040";
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single();
        TemplatePicker.SelectedIndex = 0;
        if (pack.Manifest.Id != "raspberrypi.rp2040" || TemplatePicker.Items.Count != 3 ||
            !CreateProjectButton.IsEnabled || ManufacturerOption.FromId(pack.Manifest.Vendor).Logo is null)
        {
            throw new InvalidOperationException("RP2040 C 模板、厂商图标或创建入口缺失。");
        }
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "rp2040-templates-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        var project = Path.Combine(directory, "rp2040_c");
        await services.Projects.CreateAsync(pack, "RP2040-PICO", "minimal", "rp2040_c", project);
        await OpenProjectAsync(project, CancellationToken.None);
        if (!BuildButton.IsEnabled || supportsDownload || DownloadButton.IsEnabled || DebugStartButton.IsEnabled ||
            !services.Intelligence.IsReady || await services.Downloads.ConfigurationAsync(project) is not null)
        {
            throw new InvalidOperationException("RP2040 编译/编辑未就绪，或错误开放未验收的硬件入口。");
        }
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "rp2040-c-editor.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"),
            "PASS: Raspberry Pi logo, 3 C templates, dark/light themes, project creation, build/editor enabled, hardware download/debug disabled. No hardware access.\n");
    }
}
