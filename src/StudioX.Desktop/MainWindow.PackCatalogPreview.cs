namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>隔离目录验证同 ID 新旧包的选择器去重、旧归档再次导入和实际模板显示。</summary>
    public async Task RenderPackCatalogPreviewAsync(string directory, string oldArchive, string newArchive)
    {
        await CheckPackCatalogLoadingAsync(directory, newArchive);
        var older = await services.Packs.ImportAsync(oldArchive);
        var newer = await services.Packs.ImportAsync(newArchive);
        if (newer.Manifest.Id != older.Manifest.Id || PackVersion.Compare(newer.Manifest.Version, older.Manifest.Version) <= 0)
        {
            throw new InvalidOperationException("预览需要同 ID 且版本递增的两个器件包。");
        }
        await PrunePackVersionsAsync(CancellationToken.None);
        await BeginNewProjectAsync(CancellationToken.None);
        VendorPicker.SelectedItem = VendorPicker.Items.Cast<ManufacturerOption>()
            .Single(vendor => vendor.Id == PackManufacturer(newer));
        var latest = PackPicker.Items.Cast<InstalledPack>().Single(pack => pack.Manifest.Id == newer.Manifest.Id);
        if (latest.Manifest.Version != newer.Manifest.Version)
        {
            throw new InvalidOperationException("新建工程没有默认展示最新版。");
        }
        SelectPack(latest);
        if (PackPicker.Items.Cast<InstalledPack>().Count(pack => pack.Manifest.Id == newer.Manifest.Id) != 1)
        {
            throw new InvalidOperationException("选择器仍显示冗余旧版。");
        }
        if (!PackCatalogPolicy.Supersedes(newer.Manifest, older.Manifest))
        {
            var retained = installedPacks.Single(pack => pack.Manifest.Id == older.Manifest.Id && pack.Manifest.Version == older.Manifest.Version);
            SelectPack(retained);
            await RefreshPacksAsync(CancellationToken.None, preserveSelection: true);
            if (PackPicker.SelectedItem is not InstalledPack explicitChoice || explicitChoice.Manifest.Version != older.Manifest.Version ||
                PackPicker.Items.Cast<InstalledPack>().Count(pack => pack.Manifest.Id == newer.Manifest.Id) != 1)
            {
                throw new InvalidOperationException("独立 SDK 选择或刷新丢失明确版本，或产生重复包。");
            }
        }
        var selected = await ImportPackForSelectionAsync(oldArchive, CancellationToken.None);
        if (selected.Manifest.Version != newer.Manifest.Version || !ReferenceEquals(PackPicker.SelectedItem, selected))
        {
            throw new InvalidOperationException("旧归档再次导入没有保持新版选择。");
        }
        var device = selected.Manifest.Devices.FirstOrDefault(item => item.Templates.Any(template => template.Id == "spl-freertos"))
            ?? selected.Manifest.Devices[0];
        DevicePicker.SelectedItem = device;
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().FirstOrDefault(template => template.Id == "spl-freertos")
            ?? TemplatePicker.Items.Cast<ProjectTemplate>().First();
        if (TemplatePicker.Items.Count != StudioX.Application.ZephyrTemplateCatalog.Templates(device).Count)
        {
            throw new InvalidOperationException("模板目录未按新版所选器件刷新。");
        }
        await RefreshPacksAsync(CancellationToken.None, preserveSelection: true);
        if ((DevicePicker.SelectedItem as DeviceDefinition)?.Id != device.Id || TemplatePicker.SelectedItem is null)
        {
            throw new InvalidOperationException("目录刷新丢失当前器件或模板。");
        }
        await Foundation.JsonStore.WriteAsync(Path.Combine(directory, "picker-options.json"), new
        {
            selected = ((InstalledPack)PackPicker.SelectedItem).Manifest.Version,
            options = PackPicker.Items.Cast<InstalledPack>().Select(pack => new { pack.Manifest.Id, pack.Manifest.Version }),
            retainedCatalog = installedPacks.Select(pack => new { pack.Manifest.Id, pack.Manifest.Version }),
            hardware = false
        });
        if (device.Templates.Any(template => template.Id == "freertos-mcu"))
        {
            if (device.Templates.Count(template => template.Id.StartsWith("freertos-", StringComparison.Ordinal)) != 1)
            {
                throw new InvalidOperationException("FreeRTOS 仍提供独立混合模板。");
            }
            foreach (var mode in new[] { false, true })
            {
                Ag32LogicModeCheckBox.IsChecked = mode;
                foreach (var template in device.Templates)
                {
                    TemplatePicker.SelectedItem = template;
                    if (!Ag32LogicModeCheckBox.IsEnabled || Ag32LogicModeCheckBox.IsChecked != mode)
                    {
                        throw new InvalidOperationException("切换模板改变或锁定了特殊模式选择。");
                    }
                    var plan = Engine.ProjectService.Plan(selected, device.Id, template.Id, "mode_preview", mode);
                    if ((plan.Project.Logic is not null) != mode)
                    {
                        throw new InvalidOperationException("创建计划与特殊模式复选框不同步。");
                    }
                }
                TemplatePicker.SelectedItem = device.Templates.Single(template => template.Id == "freertos-mcu");
                Ag32LogicModePanel.BringIntoView();
                UpdateLayout();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                Render(this, Path.Combine(directory, mode ? "freertos-with-logic.png" : "freertos-mcu.png"));
            }
        }
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "pack-catalog-" + theme.Id + ".png"));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"),
            $"PASS: selector shows one entry for {newer.Manifest.Id}, defaults to {newer.Manifest.Version}; old archive reimport selects latest; explicit retained SDK selection and refresh remain unique; actual device/template catalog refreshed; dark/light rendered. Isolated data; no project created or hardware accessed.\n");
    }
}
