namespace StudioX.Extensions.Abstractions;

/// <summary>语义配色与可选包内插画数据，不包含可执行界面或壁纸控制。</summary>
public sealed record PluginAppearanceTheme(string Id, string DisplayName, Dictionary<string, string> Colors)
{
    public PluginAppearanceArtwork? Artwork
    {
        get; init;
    }
}
