namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;

public partial class MainWindow
{
    /// <summary>隔离数据中导入新包、选择模板、创建工程、预览下载选择；不启动 OpenOCD。</summary>
    public async Task RenderStm32PreviewAsync(string directory, string archive, string? otherVendorArchive = null)
    {
        var pack = await ImportPackForSelectionAsync(archive, CancellationToken.None);
        ShowDocument(PackagesTab);
        if (otherVendorArchive is not null)
        {
            var other = await ImportPackForSelectionAsync(otherVendorArchive, CancellationToken.None);
            if (PackManufacturer(other) == PackManufacturer(pack)) throw new InvalidOperationException("厂商预览需要两个不同厂商的包。");
            if (PackPicker.SelectedItem is not StudioX.Packages.InstalledPack imported || imported.Manifest.Id != other.Manifest.Id)
                throw new InvalidOperationException("导入后未定位到新包。");
            if (PackPicker.Items.Cast<StudioX.Packages.InstalledPack>().Any(p => PackManufacturer(p) != PackManufacturer(other)))
                throw new InvalidOperationException("导入后包列表仍包含其他厂商。");
            DeviceSearch.Text = other.Manifest.Devices[0].Id;
            DevicePicker.SelectedIndex = 0; TemplatePicker.SelectedIndex = 0;
            if (!CreateProjectButton.IsEnabled) throw new InvalidOperationException("完成选择后创建入口未启用。");
            await Layout(); Render(this, Path.Combine(directory, "vendor-other.png"));
            foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
            {
                ApplyTheme(theme);
                VendorPicker.IsDropDownOpen = true;
                await Layout();
                var popup = (System.Windows.Controls.Primitives.Popup)VendorPicker.Template.FindName("PART_Popup", VendorPicker);
                Render((FrameworkElement)popup.Child, Path.Combine(directory, "vendor-menu-" + theme.Id + ".png"));
                VendorPicker.IsDropDownOpen = false;
                Render(VendorPicker, Path.Combine(directory, "vendor-selected-" + theme.Id + ".png"));
            }
            ApplyTheme(ThemeService.Dark);
            VendorPicker.SelectedValue = PackManufacturer(pack);
            if (PackPicker.SelectedItem is not null || DevicePicker.Items.Count != 0 || TemplatePicker.Items.Count != 0 ||
                DeviceSearch.Text.Length != 0 || DeviceSearch.IsEnabled || TemplatePicker.IsEnabled || CreateProjectButton.IsEnabled ||
                PackPicker.Items.Cast<StudioX.Packages.InstalledPack>().Any(p => PackManufacturer(p) != PackManufacturer(pack)))
                throw new InvalidOperationException("切换厂商没有过滤包并清空后续选择。");
            VendorPicker.SelectedIndex = -1;
            if (PackPicker.Items.Count != 0 || PackPicker.IsEnabled) throw new InvalidOperationException("未选厂商时不应显示全部包。");
            await Layout(); Render(this, Path.Combine(directory, "vendor-unselected.png"));
        }
        SelectPack(pack);
        var selectedDevice = pack.Manifest.Devices.FirstOrDefault(d => d.Id == "STM32F103C8") ?? pack.Manifest.Devices[0];
        DeviceSearch.Text = selectedDevice.Id + "T6";
        if (!DevicePicker.Items.Cast<StudioX.Packages.DeviceDefinition>().Any(d => d.Id == selectedDevice.Id))
            throw new InvalidOperationException("型号搜索没有匹配基础料号。");
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<StudioX.Packages.DeviceDefinition>().Single(d => d.Id == selectedDevice.Id);
        if (TemplatePicker.Items.Count != 4) throw new InvalidOperationException("没有显示四种模板。");
        TemplatePicker.SelectedIndex = 1;
        await Layout(); Render(this, Path.Combine(directory, "stm32-templates.png"));
        var project = Path.Combine(directory, "stm32_hal_freertos");
        await services.Projects.CreateAsync(pack, selectedDevice.Id, "hal-freertos", "stm32_hal_freertos", project);
        await OpenProjectAsync(project, CancellationToken.None);
        if (!supportsDownload || !DownloadButton.IsEnabled || !DownloadMenu.IsEnabled || !services.Intelligence.IsReady)
            throw new InvalidOperationException("工程的下载入口或语言服务未就绪。");
        await Layout(); Render(this, Path.Combine(directory, "stm32-editor.png"));
        var originalText = SourceEditor.Text;
        var originalSize = editorSettings.FontSize;
        var originalCaret = SourceEditor.CaretOffset;
        ZoomEditor(2); await pendingZoomSave; await Layout();
        Render(this, Path.Combine(directory, "editor-zoom.png"));
        ZoomEditor(100); await pendingZoomSave;
        if (SourceEditor.FontSize != 28) throw new InvalidOperationException("编辑器放大上限无效。");
        ZoomEditor(-100); await pendingZoomSave;
        if (SourceEditor.FontSize != 10 || SourceEditor.Text != originalText || SourceEditor.CaretOffset != originalCaret || activeEditor!.IsDirty)
            throw new InvalidOperationException("缩放改变了文档或字号下限无效。");
        ZoomEditor((int)originalSize - 10); await pendingZoomSave;
        if ((await services.EditorSettings.LoadAsync()).FontSize != originalSize)
            throw new InvalidOperationException("缩放字号未正确保存。");
        var configuration = await services.Downloads.ConfigurationAsync(project) ?? throw new InvalidOperationException("下载配置缺失。");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            var dialog = new DownloadWindow(configuration) { Owner = this, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000 };
            dialog.Show();
            foreach (var probe in configuration.OpenOcd.Probes)
            {
                dialog.ProbePicker.SelectedValue = probe.Id;
                if (dialog.SpeedInput.Text != "2000") throw new InvalidOperationException("烧录器默认速度没有同步。");
            }
            dialog.UpdateLayout(); await Layout();
            Render(dialog, Path.Combine(directory, "download-" + theme.Id + ".png")); dialog.Close();
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: package import, four templates, HAL+FreeRTOS project, library-aware language service, enabled download entries, ST-Link/DAP/J-Link selection and dark/light dialogs. Editor zoom bounds, persistence and unchanged document checked. No hardware commands executed.\n");
        if (otherVendorArchive is not null)
            await File.AppendAllTextAsync(Path.Combine(directory, "result.txt"), "PASS: manufacturer filtering, import selection, downstream selection/search reset, create-button availability and no packages before manufacturer selection.\n");
        async Task Layout() { UpdateLayout(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render); }
    }
}
