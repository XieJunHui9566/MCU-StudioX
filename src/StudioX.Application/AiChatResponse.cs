namespace StudioX.Application;

/// <summary>完整响应；流式片段归并后与非流式响应采用同一数据结构。</summary>
public sealed record AiChatResponse(string? Content, IReadOnlyList<AiToolCall> ToolCalls,
    string? FinishReason, string? Model, AiTokenUsage? Usage = null, string? ReasoningContent = null);
