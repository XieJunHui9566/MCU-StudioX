namespace StudioX.Application;

using StudioX.Foundation;

public sealed class AiSettingsService(string dataDirectory)
{
    private string SettingsPath => Path.Combine(dataDirectory, "ai.json");

    public async Task<AiSettings> LoadAsync(CancellationToken token = default)
    {
        var settings = await LoadForEditAsync(token);
        Validate(settings);
        return settings;
    }

    /// <summary>允许修正旧的不兼容配置；实际发送前仍执行完整校验。</summary>
    public Task<AiSettings> LoadForEditAsync(CancellationToken token = default) => File.Exists(SettingsPath)
        ? JsonStore.ReadAsync<AiSettings>(SettingsPath, token) : Task.FromResult(new AiSettings());

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
        {
            throw new StudioXException("AI_SETTINGS", "AI 模型或超时设置无效。");
        }
        _ = CompletionUri(settings);
    }

    public static bool SupportsReasoningEffort(AiSettings settings) =>
        CompletionUri(settings).Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);

    public static int? EffectiveContextWindowTokens(AiSettings settings)
    {
        if (settings.ContextWindowTokens is { } configured)
        {
            return configured;
        }
        if (!SupportsReasoningEffort(settings))
        {
            return null;
        }
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
        {
            throw new StudioXException("AI_ENDPOINT", "AI API 地址必须是无账号、查询参数和片段的 HTTPS 基础地址。");
        }
        if (baseUri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase) ||
            baseUri.AbsolutePath.Split('/').Any(part => part.Equals("anthropic", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("messages", StringComparison.OrdinalIgnoreCase)))
        {
            throw new StudioXException("AI_PROTOCOL", "仅支持 OpenAI 兼容的 Chat Completions API（包括 DeepSeek）；不支持 Anthropic Messages API。");
        }
        var normalized = new Uri(baseUri.AbsoluteUri.TrimEnd('/'));
        return normalized.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? normalized : new Uri(normalized.AbsoluteUri.TrimEnd('/') + "/chat/completions", UriKind.Absolute);
    }
}
