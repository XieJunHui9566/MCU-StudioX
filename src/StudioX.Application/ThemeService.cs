namespace StudioX.Application;

using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record ThemeDefinition(int FormatVersion, string Id, string DisplayName, Dictionary<string, string> Colors);
public sealed record UserPreferences(int FormatVersion, string ThemeId);
public sealed partial class ThemeService(string dataDirectory)
{
    private static readonly string[] Keys = ["Background", "Surface", "Panel", "Border", "Text", "Muted", "Accent", "OnAccent"];
    public static ThemeDefinition Dark => new(1, "studiox.dark", "StudioX Dark", new()
    { ["Background"] = "#1E1F22", ["Surface"] = "#2B2D30", ["Panel"] = "#393B40", ["Border"] = "#43454A", ["Text"] = "#DFE1E5", ["Muted"] = "#9DA0A8", ["Accent"] = "#548AF7", ["OnAccent"] = "#FFFFFF" });
    public static ThemeDefinition Light => new(1, "studiox.light", "StudioX Light", new()
    { ["Background"] = "#FFFFFF", ["Surface"] = "#F2F3F5", ["Panel"] = "#E4E6EB", ["Border"] = "#D3D5DB", ["Text"] = "#27282E", ["Muted"] = "#686B75", ["Accent"] = "#3574F0", ["OnAccent"] = "#FFFFFF" });
    public static void Validate(ThemeDefinition theme)
    {
        PackValidator.Token(theme.Id);
        if (theme.FormatVersion != 1 || string.IsNullOrWhiteSpace(theme.DisplayName) || theme.Colors is null ||
            !theme.Colors.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(Keys) || theme.Colors.Values.Any(c => c is null || !HexColor().IsMatch(c)))
            throw new StudioXException("THEME_FORMAT", "主题需要格式 1 和完整的八个 #RRGGBB 语义颜色。");
    }
    public async Task<ThemeDefinition> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(dataDirectory, "preferences.json");
        if (!File.Exists(path)) return Dark;
        var preferences = await JsonStore.ReadAsync<UserPreferences>(path, cancellationToken);
        if (preferences.FormatVersion != 1) throw new StudioXException("PREFERENCES_FORMAT", "用户偏好格式不支持。");
        PackValidator.Token(preferences.ThemeId);
        var theme = preferences.ThemeId switch
        {
            "studiox.dark" => Dark,
            "studiox.light" => Light,
            _ => await JsonStore.ReadAsync<ThemeDefinition>(PathBoundary.Resolve(dataDirectory, "themes/" + preferences.ThemeId + ".json"), cancellationToken)
        };
        Validate(theme);
        return theme;
    }
    public async Task SelectAsync(ThemeDefinition theme, CancellationToken cancellationToken = default)
    {
        Validate(theme);
        if (theme.Id is not ("studiox.dark" or "studiox.light"))
            await JsonStore.WriteAsync(PathBoundary.Resolve(dataDirectory, "themes/" + theme.Id + ".json"), theme, cancellationToken);
        await JsonStore.WriteAsync(Path.Combine(dataDirectory, "preferences.json"), new UserPreferences(1, theme.Id), cancellationToken);
    }
    public async Task<ThemeDefinition> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var theme = await JsonStore.ReadAsync<ThemeDefinition>(path, cancellationToken);
        if (theme.Id is "studiox.dark" or "studiox.light") throw new StudioXException("THEME_RESERVED", "自定义主题不能覆盖内置主题 ID。");
        await SelectAsync(theme, cancellationToken);
        return theme;
    }
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)] private static partial Regex HexColor();
}
