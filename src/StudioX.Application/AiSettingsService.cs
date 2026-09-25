namespace StudioX.Application;

using StudioX.Foundation;

/// <summary>用户的模型连接偏好。API Key 始终存于 Windows 凭据管理器，不属于此设置。</summary>
public sealed record AiSettings(int FormatVersion = 1, string BaseUrl = "https://api.deepseek.com",
    string Model = "deepseek-flash", int TimeoutSeconds = 120,
    string ReasoningEffort = "high", int? ContextWindowTokens = null);

public sealed class AiSettingsService(string dataDirectory)
{
    private string SettingsPath => Path.Combine(dataDirectory, "ai.json");

    public async Task<AiSettings> LoadAsync(CancellationToken token = default)
    {
        var settings = File.Exists(SettingsPath) ? await JsonStore.ReadAsync<AiSettings>(SettingsPath, token) : new();
        Validate(settings);
        return settings;
    }

    public Task SaveAsync(AiSettings settings, CancellationToken token = default)
    {
        Validate(settings);
        return JsonStore.WriteAsync(SettingsPath, settings, token);
    }

    public static void Validate(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.FormatVersion != 1 || settings.TimeoutSeconds is < 10 or > 600 ||
            string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 200 ||
            settings.Model.Any(char.IsWhiteSpace) || settings.Model.Any(char.IsControl) ||
            settings.ReasoningEffort is not ("none" or "low" or "high" or "max") ||
            settings.ContextWindowTokens is < 1 or > 2_000_000)
            throw new StudioXException("AI_SETTINGS", "AI 模型或超时设置无效。");
        _ = CompletionUri(settings);
    }

    public static bool SupportsReasoningEffort(AiSettings settings) =>
        CompletionUri(settings).Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);

    public static int? EffectiveContextWindowTokens(AiSettings settings)
    {
        if (settings.ContextWindowTokens is { } configured)
            return configured;
        if (!SupportsReasoningEffort(settings))
            return null;
        return settings.Model is "deepseek-flash" or "deepseek-v4-pro" ? 1_000_000 : null;
    }

    public static Uri CompletionUri(AiSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || settings.BaseUrl.Length > 2048 ||
            settings.BaseUrl.Contains('?') || settings.BaseUrl.Contains('#') ||
            !Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(baseUri.UserInfo) || !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
            throw new StudioXException("AI_ENDPOINT", "AI API 地址必须是无账号、查询参数和片段的 HTTPS 基础地址。");
        return new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/chat/completions", UriKind.Absolute);
    }
}
