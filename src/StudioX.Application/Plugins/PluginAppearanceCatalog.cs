namespace StudioX.Application.Plugins;

using System.Text.Json;
using System.Text.Json.Serialization;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>校验配色与包内 PNG 插画；拒绝壁纸控制、远程资源和任意界面代码。</summary>
public static class PluginAppearanceCatalog
{
    public static PluginAppearanceDefinition Parse(JsonElement value)
    {
        PluginContributionValidator.ValidateJson(value, 64 * 1024);
        var options = new JsonSerializerOptions(JsonStore.Options)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        var catalog = value.Deserialize<PluginAppearanceDefinition>(options);
        if (catalog is null || catalog.FormatVersion is not (1 or 2) || catalog.Themes is null || catalog.Themes.Length is < 1 or > 32)
        {
            throw new StudioXException("PLUGIN_APPEARANCE", "外观目录需要格式 1/2 和 1 到 32 个主题。");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var theme in catalog.Themes)
        {
            if (theme is null || !ids.Add(theme.Id) || theme.Id is "studiox.dark" or "studiox.light" ||
                string.IsNullOrWhiteSpace(theme.DisplayName) || theme.DisplayName.Length > 80 || theme.DisplayName.Any(char.IsControl))
            {
                throw new StudioXException("PLUGIN_APPEARANCE", "配色 ID 必须唯一且不能覆盖内置主题，名称需为 1 到 80 个可见字符。");
            }
            ThemeService.Validate(new(1, theme.Id, theme.DisplayName, theme.Colors));
            if (theme.Artwork is not null)
            {
                if (catalog.FormatVersion != 2)
                {
                    throw new StudioXException("PLUGIN_APPEARANCE", "插画需要显式使用外观目录格式 2。");
                }
                ThemeArtworkService.ValidatePlugin(theme.Artwork);
            }
        }
        return catalog;
    }

    public static ThemeDefinition ToTheme(PluginAppearanceTheme theme) => new(1, theme.Id, theme.DisplayName, theme.Colors)
    {
        Artwork = theme.Artwork is null ? null : ThemeArtworkService.FromPlugin(theme.Artwork)
    };
}
