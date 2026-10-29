namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>在隔离工作台核对 MCU 后的框架选择和板级编辑入口，不连接设备。</summary>
    public async Task RenderZephyrTemplatesPreviewAsync(string directory, string archive, string project)
    {
        var pack = await services.Packs.ImportAsync(archive);
        await RefreshPacksAsync(CancellationToken.None);
        SetProjectDetailsMode(null);
        ShowDocument(PackagesTab);
        SelectPack(pack);
        DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single(d => d.Id == "STM32F407ZG");
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().Single(ZephyrTemplateCatalog.IsZephyr);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!CreateProjectButton.IsEnabled || ArduinoTemplatePanel.Visibility != Visibility.Collapsed ||
            IdfVersionPanel.Visibility != Visibility.Collapsed || !SelectedDevelopmentComponents.Text.Contains("自定义板级", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Zephyr 未作为 MCU 的普通起始模板显示。");
        }
        UpdateLayout();
        Render(this, Path.Combine(directory, "zephyr-template.png"));
        await OpenProjectAsync(project, CancellationToken.None);
        if (currentProjectManifest is not { Kind: ProjectKind.Zephyr, Zephyr.FormatVersion: 2 } ||
            !BuildButton.IsEnabled || DownloadButton.IsEnabled || !ZephyrDeviceTreeButton.IsEnabled ||
            activeDocument?.RelativePath != currentProjectManifest.Zephyr.Devicetree || SourceEditor.IsReadOnly)
        {
            throw new InvalidOperationException("Zephyr 构建、硬件隔离或板级 DTS 编辑状态不正确。");
        }
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "zephyr-project.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"),
            "PASS MCU-first Zephyr template; no Arduino/IDF selectors; build enabled; hardware disabled; editable project-owned DTS.\n");
    }
}
