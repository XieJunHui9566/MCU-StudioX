namespace StudioX.Application;

/// <summary>一轮请求的累计实际用量；字段缺失的请求不按零估算。</summary>
public sealed record AiAgentUsageTotals(long? PromptTokens, long? CompletionTokens,
    long? TotalTokens, long? PromptCacheHitTokens, long? PromptCacheMissTokens,
    int RequestsWithUsage, int RequestsWithCacheDetails);
