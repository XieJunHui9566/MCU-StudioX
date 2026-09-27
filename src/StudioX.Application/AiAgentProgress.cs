namespace StudioX.Application;

/// <summary>可见进度只包含模型实际返回的文本和真实工具阶段，不推测内部思维。</summary>
public sealed record AiAgentProgress(AiAgentProgressKind Kind, int Round,
    string? Text = null, string? ToolName = null);
