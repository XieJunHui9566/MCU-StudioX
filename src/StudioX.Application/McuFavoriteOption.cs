namespace StudioX.Application;

using StudioX.Packages;

public sealed record McuFavoriteOption(McuFavorite Favorite, InstalledPack? Pack, DeviceDefinition? Device)
{
    public bool IsAvailable => Pack is not null && Device is not null;
    public string Label => $"{Favorite.DisplayName} · {Favorite.PackDisplayName}"
        + (IsAvailable ? "" : "（器件包或型号未安装）");
}
