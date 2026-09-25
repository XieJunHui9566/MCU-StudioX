namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine.Debugging;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>隔离数据中验证 C 模板与下载/调试入口；不连接硬件。</summary>
    public async Task RenderRp2350PreviewAsync(string directory, string archive)
    {
        var pack = await ImportPackForSelectionAsync(archive, CancellationToken.None);
        ShowDocument(PackagesTab);
        SelectPack(pack);
        DeviceSearch.Text = "RP2350";
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single();
        TemplatePicker.SelectedIndex = 0;
        if (TemplatePicker.Items.Count != 3 || !CreateProjectButton.IsEnabled ||
            ManufacturerOption.FromId(pack.Manifest.Vendor).Logo is null)
            throw new InvalidOperationException("RP2350 C 模板、厂商图标或创建入口缺失。");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "rp2350-templates-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        var project = Path.Combine(directory, "rp2350_c");
        await services.Projects.CreateAsync(pack, Rp2350DebugTarget.DeviceId, "minimal", "rp2350_c", project);
        await OpenProjectAsync(project, CancellationToken.None);
        var configuration = await services.Downloads.ConfigurationAsync(project) ?? throw new InvalidOperationException("缺少下载配置。");
        if (!supportsDownload || !DownloadButton.IsEnabled || !services.Intelligence.IsReady ||
            configuration.Options.SpeedKhz != 1000 || OpenOcdDebugPlanner.ResolveProbe(configuration).Id != "cmsis-dap")
            throw new InvalidOperationException("RP2350 下载、调试或语言服务入口未就绪。");
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "rp2350-c-editor.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: Raspberry Pi logo, 3 C templates, new project, language service and CMSIS-DAP download/debug configuration. No hardware access.\n");
    }
}
