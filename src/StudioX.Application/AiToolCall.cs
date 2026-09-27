namespace StudioX.Application;

/// <summary>模型请求执行的工具调用；参数在工具入口校验，不能仅凭模型输出执行。</summary>
public sealed record AiToolCall(string Id, string Name, string ArgumentsJson);
