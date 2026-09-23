namespace StudioX.Application;

using StudioX.Foundation;

public sealed record EditorSettings(int FormatVersion = 1, string FontFamily = "Cascadia Mono", double FontSize = 14, bool SoftGlow = true, double GlowStrength = 80);

public sealed class EditorSettingsService(string dataDirectory)
{
    private string SettingsPath => Path.Combine(dataDirectory, "editor.json");
    public async Task<EditorSettings> LoadAsync(CancellationToken token = default)
    {
        var settings = File.Exists(SettingsPath) ? await JsonStore.ReadAsync<EditorSettings>(SettingsPath, token) : new();
        Validate(settings); return settings;
    }
    public Task SaveAsync(EditorSettings settings, CancellationToken token = default)
    {
        Validate(settings); return JsonStore.WriteAsync(SettingsPath, settings, token);
    }
    public static void Validate(EditorSettings settings)
    {
        if (settings.FormatVersion != 1 || string.IsNullOrWhiteSpace(settings.FontFamily) || settings.FontFamily.Length > 100 ||
            !double.IsFinite(settings.FontSize) || settings.FontSize is < 10 or > 28 ||
            !double.IsFinite(settings.GlowStrength) || settings.GlowStrength is < 0 or > 100)
            throw new StudioXException("EDITOR_SETTINGS", "编辑器设置无效，字号应在 10 到 28 之间，柔光强度应在 0 到 100 之间。");
    }
}
