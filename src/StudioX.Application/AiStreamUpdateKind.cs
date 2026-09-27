namespace StudioX.Application;

/// <summary>流式通知的类别；工具名称通知不代表调用已执行。</summary>
public enum AiStreamUpdateKind
{
    ReasoningDelta,
    ContentDelta,
    ToolCall,
    Usage
}
