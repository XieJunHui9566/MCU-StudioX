namespace StudioX.Application;

/// <summary>模型流式增量；工具事件只报告名称，不把尚未验证的参数显示为操作结果。</summary>
public sealed record AiStreamUpdate(AiStreamUpdateKind Kind, string? Text = null,
    string? ToolName = null, int? ToolCallIndex = null, AiTokenUsage? Usage = null);
