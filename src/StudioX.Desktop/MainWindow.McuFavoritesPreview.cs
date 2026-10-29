namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>用隔离的格式 1 器件包验证收藏与新建工程选择器，不使用工具链或连接硬件。</summary>
    public async Task RenderMcuFavoritesPreviewAsync(string directory)
    {
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add("PASS: " + message);
        }
        async Task<InstalledPack> Fixture(string id, string vendor, string version, string model)
        {
            var source = Path.Combine(directory, id + "-" + version);
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "main.c"), "int main(void) { for (;;) {} }\n");
            await File.WriteAllTextAsync(Path.Combine(source, "link.ld"), "MEMORY { FLASH(rx): ORIGIN=0x08000000, LENGTH=128K }\n");
            var device = new DeviceDefinition(model, model, "arm", 0x08000000, 131072, 0x20000000, 20480,
                "test.toolset", "1.0.0", "test-gcc", [], [], [], [], "link.ld", [], [],
                [new ProjectTemplate("bare", "基础工程", "仅用于收藏界面验证", "main.c")]);
            await JsonStore.WriteAsync(Path.Combine(source, "manifest.json"), new PackManifest(1, id, version, vendor + " 测试器件包", vendor, [device]));
            var archive = source + ".mcupack";
            await PackArchiveWriter.WriteAsync(source, archive);
            return await services.Packs.ImportAsync(archive);
        }
        var first = await Fixture("test.favorites-st", "STMicroelectronics", "1.0.0", "STM32F103C8T6");
        var other = await Fixture("test.favorites-wch", "WCH", "1.0.0", "CH32V203C8T6");
        SetProjectDetailsMode(null);
        ShowDocument(PackagesTab);
        await RefreshPacksAsync(CancellationToken.None);
        Check(!McuFavoritesPicker.IsEnabled && !ToggleMcuFavoriteButton.IsEnabled, "empty favorites and no device start disabled");
        SelectPack(installedPacks.Single(pack => pack.Manifest.Id == first.Manifest.Id));
        DevicePicker.SelectedIndex = 0;
        Check(ToggleMcuFavoriteButton.IsEnabled, "selecting a real device enables favorite button");
        await ToggleSelectedMcuFavoriteAsync();
        Check(McuFavoritesPicker.Items.Count == 1 && ToggleMcuFavoriteButton.Content.ToString() == "★ 已收藏", "selected device is saved and marked favorite");
        SelectPack(installedPacks.Single(pack => pack.Manifest.Id == other.Manifest.Id));
        DevicePicker.SelectedIndex = 0;
        await ToggleSelectedMcuFavoriteAsync();
        Check((await new McuFavoritesService(services.DataDirectory).LoadAsync()).Count == 2, "favorites survive new service instance");
        DeviceSearch.Text = "no-match";
        McuFavoritesPicker.SelectedItem = mcuFavoriteOptions.Single(item => item.Favorite.PackId == first.Manifest.Id);
        Check((PackPicker.SelectedItem as InstalledPack)?.Manifest.Id == first.Manifest.Id &&
            (DevicePicker.SelectedItem as DeviceDefinition)?.Id == first.Manifest.Devices[0].Id && DeviceSearch.Text == "",
            "favorite selects exact device across vendor and clears conflicting search");
        Check(TemplatePicker.Items.Count == 1 && TemplatePicker.SelectedIndex == -1 && !CreateProjectButton.IsEnabled,
            "favorite refreshes supported templates and requires explicit template choice");
        TemplatePicker.SelectedIndex = 0;
        await RefreshPacksAsync(CancellationToken.None, preserveSelection: true);
        Check((DevicePicker.SelectedItem as DeviceDefinition)?.Id == first.Manifest.Devices[0].Id && TemplatePicker.SelectedIndex == 0,
            "background catalog refresh preserves device and template even with active favorite");
        await Fixture(first.Manifest.Id, first.Manifest.Vendor, "1.1.0", first.Manifest.Devices[0].Id);
        await RefreshPacksAsync(CancellationToken.None);
        McuFavoritesPicker.SelectedIndex = -1;
        McuFavoritesPicker.SelectedItem = mcuFavoriteOptions.Single(item => item.Favorite.PackId == first.Manifest.Id);
        Check((PackPicker.SelectedItem as InstalledPack)?.Manifest.Version == "1.1.0", "favorite uses installed update with same package and model identity");
        DeviceSearch.Text = "no-match";
        Check(McuFavoritesPicker.SelectedIndex == -1 && !ToggleMcuFavoriteButton.IsEnabled, "manual filtering clears stale favorite selection");
        McuFavoritesPicker.SelectedItem = mcuFavoriteOptions.Single(item => item.Favorite.PackId == first.Manifest.Id);
        Check(DevicePicker.SelectedItem is DeviceDefinition, "same favorite can be reapplied after manual search");
        TemplatePicker.SelectedIndex = 0;
        var selectedPack = (InstalledPack)PackPicker.SelectedItem;
        var selectedDevice = (DeviceDefinition)DevicePicker.SelectedItem;
        var selectedTemplate = (ProjectTemplate)TemplatePicker.SelectedItem;
        var created = await services.Projects.CreateAsync(selectedPack, selectedDevice.Id, selectedTemplate.Id,
            "favorite_project", Path.Combine(directory, "created-project"));
        Check(created.PackId == first.Manifest.Id && created.PackVersion == "1.1.0" && created.DeviceId == selectedDevice.Id &&
            File.Exists(Path.Combine(directory, "created-project", "CMakeLists.txt")), "favorite selection creates project with exact model and package version");
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "mcu-favorites-" + theme.Id + ".png"));
        }
        var unavailable = new McuFavorite("test.not-installed", "MISSING-MCU", "MISSING-MCU", "未安装的测试器件包", "Test");
        // 通过实际目录解析生成缺包选项，界面不得继续保留上一型号的创建能力。
        var ghost = new InstalledPack(new(1, unavailable.PackId, "1.0.0", unavailable.PackDisplayName, unavailable.Vendor,
            [first.Manifest.Devices[0] with { Id = unavailable.DeviceId, DisplayName = unavailable.DisplayName }]), directory, "fixture");
        await services.McuFavorites.AddAsync(ghost, ghost.Manifest.Devices[0]);
        await RefreshMcuFavoritesAsync(CancellationToken.None);
        McuFavoritesPicker.SelectedItem = mcuFavoriteOptions.Single(item => item.Favorite.PackId == unavailable.PackId);
        Check(DevicePicker.SelectedItem is null && !CreateProjectButton.IsEnabled && RemoveMcuFavoriteButton.IsEnabled && McuFavoritesHint.Text.Contains("未安装"),
            "missing package is explained, can be removed, and cannot create wrong device");
        await ChangeMcuFavoritesAsync(() => services.McuFavorites.RemoveAsync(unavailable));
        Check(McuFavoritesPicker.Items.Count == 2, "missing favorite removal persists");
        McuFavoritesPicker.SelectedItem = mcuFavoriteOptions.Single(item => item.Favorite.PackId == first.Manifest.Id);
        await ToggleSelectedMcuFavoriteAsync();
        Check(McuFavoritesPicker.Items.Count == 1 && ToggleMcuFavoriteButton.Content.ToString() == "☆ 收藏", "star button removes current favorite");
        await services.McuFavorites.RemoveAsync((await services.McuFavorites.LoadAsync()).Single());
        await RefreshMcuFavoritesAsync(CancellationToken.None);
        Check(!McuFavoritesPicker.IsEnabled && !RemoveMcuFavoriteButton.IsEnabled, "last removal restores empty state");
        await File.WriteAllTextAsync(Path.Combine(services.DataDirectory, "mcu-favorites.json"), "{");
        await RefreshMcuFavoritesAsync(CancellationToken.None);
        Check(!ToggleMcuFavoriteButton.IsEnabled && DevicePicker.IsEnabled && McuFavoritesHint.Text.Contains("读取失败"),
            "damaged preference disables mutations while manual device selection remains usable");
        await File.WriteAllLinesAsync(Path.Combine(directory, "result.txt"), checks);
    }
}
