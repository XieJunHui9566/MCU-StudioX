namespace StudioX.Application;

/// <summary>可重放的协议消息；StudioXKind 仅供本地上下文整理，不发送到 API。</summary>
public sealed record AiChatMessage(string Role, string? Content, string? ToolCallId = null,
    IReadOnlyList<AiToolCall>? ToolCalls = null, string? ReasoningContent = null,
    string? StudioXKind = null);
