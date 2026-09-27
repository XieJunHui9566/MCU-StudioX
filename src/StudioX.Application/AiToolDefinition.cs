namespace StudioX.Application;

/// <summary>发送给模型的工具名称、用途和 JSON 参数模式。</summary>
public sealed record AiToolDefinition(string Name, string Description, string ParametersJson);
