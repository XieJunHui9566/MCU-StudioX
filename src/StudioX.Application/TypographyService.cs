namespace StudioX.Application;

using StudioX.Foundation;

/// <summary>持久化界面字体，只接受字体名称，不加载路径、URI 或插件字体文件。</summary>
public sealed class TypographyService(string dataDirectory)
{
    private string SettingsPath => Path.Combine(dataDirectory, "typography.json");

    public async Task<TypographySettings> LoadAsync(CancellationToken token = default)
    {
        var settings = File.Exists(SettingsPath) ? await JsonStore.ReadAsync<TypographySettings>(SettingsPath, token) : new();
        Validate(settings);
        return settings;
    }

    public Task SaveAsync(TypographySettings settings, CancellationToken token = default)
    {
        Validate(settings);
        return JsonStore.WriteAsync(SettingsPath, settings, token);
    }

    public static void Validate(TypographySettings settings)
    {
        ValidateFontFamily(settings.FontFamily);
        if (settings.FormatVersion != 1 || !double.IsFinite(settings.FontSize) || settings.FontSize is < 11 or > 18)
        {
            throw new StudioXException("TYPOGRAPHY_SETTINGS", "界面字号应在 11 到 18 之间。");
        }
    }

    public static void ValidateFontFamily(string family)
    {
        if (string.IsNullOrWhiteSpace(family) || family.Length > 100 || family.Any(char.IsControl) ||
            family.IndexOfAny(['/', '\\', ':', '#', ';']) >= 0)
        {
            throw new StudioXException("TYPOGRAPHY_FONT", "字体需为 1 到 100 个字符的本地字体名称，不接受路径或 URI。");
        }
    }
}
