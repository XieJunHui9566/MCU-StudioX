namespace StudioX.Application;

/// <summary>包含可见对话及协议回放消息；工具结果可含工程文本，凭据不进入历史文件。</summary>
public sealed record AiConversation(string Id, string Title, DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc, IReadOnlyList<AiAgentTurn> Turns,
    int? LastPromptTokens = null, int? LastCompletionTokens = null,
    long? LastPromptCacheHitTokens = null, long? LastPromptCacheMissTokens = null);
