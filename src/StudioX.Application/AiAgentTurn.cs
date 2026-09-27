namespace StudioX.Application;

/// <summary>保存可见对话及 API 协议消息；旧记录没有 ProtocolMessages 时仍可读取。</summary>
public sealed record AiAgentTurn(string User, string Assistant, string? ReasoningContent = null,
    IReadOnlyList<AiChatMessage>? ProtocolMessages = null, string? ContextSummary = null,
    long CompactedToolCalls = 0, IReadOnlyList<string>? SteeringMessages = null);
