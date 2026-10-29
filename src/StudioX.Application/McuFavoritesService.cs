namespace StudioX.Application;

using StudioX.Foundation;
using StudioX.Packages;

/// <summary>用户级收藏的持久化和目录解析；不访问硬件或代替创建工程前的完整包校验。</summary>
public sealed class McuFavoritesService(string dataDirectory)
{
    private readonly string path = Path.Combine(dataDirectory, "mcu-favorites.json");
    private readonly SemaphoreSlim mutationGate = new(1, 1);

    public async Task<IReadOnlyList<McuFavorite>> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        var document = await JsonStore.ReadAsync<FavoritesDocument>(path, token);
        if (document.FormatVersion != 1 || document.Items is null || document.Items.Any(item => item is null ||
            string.IsNullOrWhiteSpace(item.PackId) || string.IsNullOrWhiteSpace(item.DeviceId) ||
            string.IsNullOrWhiteSpace(item.DisplayName) || string.IsNullOrWhiteSpace(item.PackDisplayName) ||
            string.IsNullOrWhiteSpace(item.Vendor)))
        {
            throw new StudioXException("MCU_FAVORITES_FORMAT", "MCU 收藏文件格式无效或版本不受支持，原文件已保留。");
        }
        return document.Items.DistinctBy(item => (item.PackId, item.DeviceId)).ToArray();
    }

    public async Task AddAsync(InstalledPack pack, DeviceDefinition device, CancellationToken token = default)
    {
        // 型号必须来自这个包，不能把自由文本或另一个包的同名型号记入收藏。
        if (!pack.Manifest.Devices.Contains(device))
        {
            throw new StudioXException("MCU_FAVORITE_DEVICE", "所选 MCU 不属于指定器件包。");
        }
        var favorite = new McuFavorite(pack.Manifest.Id, device.Id, device.DisplayName, pack.Manifest.DisplayName, pack.Manifest.Vendor);
        await mutationGate.WaitAsync(token);
        try
        {
            var existing = await LoadAsync(token);
            await JsonStore.WriteAsync(path, new FavoritesDocument(1,
                existing.Where(item => !SameIdentity(item, favorite)).Append(favorite).ToArray()), token);
        }
        finally { mutationGate.Release(); }
    }

    public async Task RemoveAsync(McuFavorite favorite, CancellationToken token = default)
    {
        await mutationGate.WaitAsync(token);
        try
        {
            var existing = await LoadAsync(token);
            await JsonStore.WriteAsync(path, new FavoritesDocument(1,
                existing.Where(item => !SameIdentity(item, favorite)).ToArray()), token);
        }
        finally { mutationGate.Release(); }
    }

    public async Task<IReadOnlyList<McuFavoriteOption>> OptionsAsync(IReadOnlyList<InstalledPack> catalog, CancellationToken token = default)
    {
        var favorites = await LoadAsync(token);
        return favorites.Select(favorite =>
        {
            // 只在同包 ID 中找准确型号。新版移除型号时仍允许显式使用尚存的旧包。
            var pack = catalog.Where(item => item.Manifest.Id == favorite.PackId &&
                item.Manifest.Devices.Any(device => device.Id == favorite.DeviceId))
                .OrderByDescending(item => item.Manifest.Version, Comparer<string>.Create(PackVersion.Compare)).FirstOrDefault();
            return new McuFavoriteOption(favorite, pack, pack?.Manifest.Devices.Single(device => device.Id == favorite.DeviceId));
        }).OrderBy(item => item.Favorite.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Favorite.PackId, StringComparer.Ordinal).ToArray();
    }

    private static bool SameIdentity(McuFavorite left, McuFavorite right) => left.PackId == right.PackId && left.DeviceId == right.DeviceId;
    private sealed record FavoritesDocument(int FormatVersion, IReadOnlyList<McuFavorite> Items);
}
