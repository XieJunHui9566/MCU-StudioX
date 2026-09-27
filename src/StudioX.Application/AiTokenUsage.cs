namespace StudioX.Application;

/// <summary>API 报告的实际 token 数；字段缺失时保持未知，不从文本长度推算。</summary>
public sealed record AiTokenUsage(int? PromptTokens, int? CompletionTokens, int? TotalTokens,
    int? PromptCacheHitTokens = null, int? PromptCacheMissTokens = null);
