namespace StudioX.Application;

/// <summary>用户的模型连接偏好。API Key 始终存于 Windows 凭据管理器，不属于此设置。</summary>
public sealed record AiSettings(int FormatVersion = 1, string BaseUrl = "https://api.deepseek.com",
    string Model = "deepseek-flash", int TimeoutSeconds = 120,
    string ReasoningEffort = "high", int? ContextWindowTokens = null);
