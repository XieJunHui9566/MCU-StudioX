namespace StudioX.Application.CodeIntelligence;

/// <summary>诊断只属于一个语言会话中的一个文本版本，界面必须再核对编辑缓冲区。</summary>
public sealed record CodeDiagnosticBatch(string Project, string Path, string Text, int Version,
    IReadOnlyList<CodeDiagnostic> Items)
{
    /// <summary>无效范围或数量截断时保留有效条目，但不能把剩余零条目表述为检查通过。</summary>
    public bool IsComplete { get; init; } = true;
}
