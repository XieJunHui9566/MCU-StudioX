namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>隔离目录验证同 ID 新旧包的选择器去重、旧归档再次导入和实际模板显示。</summary>
    public async Task RenderPackCatalogPreviewAsync(string directory, string oldArchive, string newArchive)
    {
        var older = await services.Packs.ImportAsync(oldArchive);
        var newer = await services.Packs.ImportAsync(newArchive);
        if (!PackCatalogPolicy.Supersedes(newer.Manifest, older.Manifest))
            throw new InvalidOperationException("预览需要新版完整覆盖旧版的同 ID 器件包。");
        await BeginNewProjectAsync(CancellationToken.None);
        SelectPack(installedPacks.Single(pack => pack.Manifest.Id == newer.Manifest.Id));
        if (PackPicker.Items.Cast<InstalledPack>().Count(pack => pack.Manifest.Id == newer.Manifest.Id) != 1)
            throw new InvalidOperationException("选择器仍显示冗余旧版。");
        var selected = await ImportPackForSelectionAsync(oldArchive, CancellationToken.None);
        if (selected.Manifest.Version != newer.Manifest.Version || !ReferenceEquals(PackPicker.SelectedItem, selected))
            throw new InvalidOperationException("旧归档再次导入没有保持新版选择。");
        var device = selected.Manifest.Devices.FirstOrDefault(item => item.Templates.Any(template => template.Id == "spl-freertos"))
            ?? selected.Manifest.Devices[0];
        DevicePicker.SelectedItem = device;
        TemplatePicker.SelectedItem = TemplatePicker.Items.Cast<ProjectTemplate>().FirstOrDefault(template => template.Id == "spl-freertos")
            ?? TemplatePicker.Items.Cast<ProjectTemplate>().First();
        if (TemplatePicker.Items.Count != device.Templates.Count)
            throw new InvalidOperationException("模板目录未按新版所选器件刷新。");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "pack-catalog-" + theme.Id + ".png"));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"),
            $"PASS: selector retains {newer.Manifest.Id}/{newer.Manifest.Version}; importing old archive keeps current selection; actual device/template catalog refreshed; dark/light rendered. Isolated data; no project created or hardware accessed.\n");
    }
}
