namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Packages;

public partial class MainWindow
{
    private McuFavoriteOption[] mcuFavoriteOptions = [];
    private bool mcuFavoritesLoaded;
    private bool mcuFavoritesBusy;
    private bool updatingMcuFavorites;
    private bool selectingMcuFavorite;
    private int mcuFavoritesRevision;

    private async Task RefreshMcuFavoritesAsync(CancellationToken token)
    {
        var revision = ++mcuFavoritesRevision;
        try
        {
            var options = await services.McuFavorites.OptionsAsync(installedPacks, token);
            if (revision != mcuFavoritesRevision)
            {
                return;
            }
            mcuFavoritesLoaded = true;
            var selected = (McuFavoritesPicker.SelectedItem as McuFavoriteOption)?.Favorite;
            mcuFavoriteOptions = options.ToArray();
            updatingMcuFavorites = true;
            try
            {
                McuFavoritesPicker.ItemsSource = mcuFavoriteOptions;
                McuFavoritesPicker.SelectedItem = mcuFavoriteOptions.FirstOrDefault(item => item.Favorite == selected);
            }
            finally { updatingMcuFavorites = false; }
            McuFavoritesHint.Text = options.Count == 0 ? "选择型号后点击“☆ 收藏”，下次可从这里快速选择。"
                : $"已收藏 {options.Count} 个 MCU。选择后自动定位厂商、器件包和准确型号，再选择起始模板。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log("MCU 收藏读取失败：" + ex);
            if (revision != mcuFavoritesRevision)
            {
                return;
            }
            mcuFavoritesLoaded = false;
            McuFavoritesHint.Text = "收藏读取失败，原文件已保留；仍可手动选择 MCU。";
        }
        UpdateMcuFavoriteControls();
    }

    private void UpdateMcuFavoriteControls()
    {
        if (ToggleMcuFavoriteButton is null || McuFavoritesPicker is null || RemoveMcuFavoriteButton is null)
        {
            return;
        }
        var pack = PackPicker.SelectedItem as InstalledPack;
        var device = DevicePicker.SelectedItem as DeviceDefinition;
        if (!updatingMcuFavorites && !selectingMcuFavorite && McuFavoritesPicker.SelectedItem is McuFavoriteOption selected &&
            selected.IsAvailable && (selected.Favorite.PackId != pack?.Manifest.Id || selected.Favorite.DeviceId != device?.Id))
        {
            McuFavoritesPicker.SelectedIndex = -1;
        }
        var isFavorite = pack is not null && device is not null && mcuFavoriteOptions.Any(item =>
            item.Favorite.PackId == pack.Manifest.Id && item.Favorite.DeviceId == device.Id);
        ToggleMcuFavoriteButton.Content = isFavorite ? "★ 已收藏" : "☆ 收藏";
        ToggleMcuFavoriteButton.ToolTip = isFavorite ? "取消收藏当前 MCU" : "收藏当前 MCU";
        ToggleMcuFavoriteButton.IsEnabled = mcuFavoritesLoaded && !mcuFavoritesBusy && pack is not null && device is not null;
        McuFavoritesPicker.IsEnabled = mcuFavoritesLoaded && !mcuFavoritesBusy && mcuFavoriteOptions.Length > 0;
        RemoveMcuFavoriteButton.IsEnabled = mcuFavoritesLoaded && !mcuFavoritesBusy && McuFavoritesPicker.SelectedItem is McuFavoriteOption;
    }

    private void McuFavoritesPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingMcuFavorites)
        {
            return;
        }
        if (McuFavoritesPicker.SelectedItem is not McuFavoriteOption option)
        {
            UpdateMcuFavoriteControls();
            return;
        }
        if (!option.IsAvailable)
        {
            SelectPack(null);
            McuFavoritesHint.Text = $"{option.Favorite.DisplayName} 的器件包或型号未安装。请先导入 {option.Favorite.PackDisplayName}，也可移除此收藏。";
            UpdateMcuFavoriteControls();
            return;
        }
        selectingMcuFavorite = true;
        try
        {
            SelectPack(option.Pack);
            DeviceSearch.Clear();
            DevicePicker.SelectedItem = DevicePicker.Items.Cast<DeviceDefinition>().Single(device => device.Id == option.Favorite.DeviceId);
        }
        finally { selectingMcuFavorite = false; }
        McuFavoritesHint.Text = $"已选择 {option.Device!.DisplayName} · {option.Pack!.Manifest.DisplayName} v{option.Pack.Manifest.Version}。请选择起始模板。";
        UpdateMcuFavoriteControls();
    }

    private async void ToggleMcuFavorite_Click(object sender, RoutedEventArgs e) => await ToggleSelectedMcuFavoriteAsync();

    private async Task ToggleSelectedMcuFavoriteAsync()
    {
        if (PackPicker.SelectedItem is InstalledPack pack && DevicePicker.SelectedItem is DeviceDefinition device)
        {
            var existing = mcuFavoriteOptions.FirstOrDefault(item => item.Favorite.PackId == pack.Manifest.Id && item.Favorite.DeviceId == device.Id);
            await ChangeMcuFavoritesAsync(() => existing is null ? services.McuFavorites.AddAsync(pack, device) : services.McuFavorites.RemoveAsync(existing.Favorite));
        }
    }

    private async void RemoveMcuFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (McuFavoritesPicker.SelectedItem is McuFavoriteOption option)
        {
            await ChangeMcuFavoritesAsync(() => services.McuFavorites.RemoveAsync(option.Favorite));
        }
    }

    private async Task ChangeMcuFavoritesAsync(Func<Task> change)
    {
        if (!mcuFavoritesLoaded || mcuFavoritesBusy)
        {
            return;
        }
        mcuFavoritesBusy = true;
        UpdateMcuFavoriteControls();
        try
        {
            await change();
            await RefreshMcuFavoritesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            McuFavoritesHint.Text = "收藏保存失败，请查看输出中的详细原因。";
            Log("MCU 收藏保存失败：" + ex);
        }
        finally
        {
            mcuFavoritesBusy = false;
            UpdateMcuFavoriteControls();
        }
    }
}
