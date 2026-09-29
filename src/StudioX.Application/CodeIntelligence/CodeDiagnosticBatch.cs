namespace StudioX.Application.CodeIntelligence;

/// <summary>诊断只属于一个语言会话中的一个文本版本，界面必须再核对编辑缓冲区。</summary>
public sealed record CodeDiagnosticBatch(string Project, string Path, string Text, int Version,
    IReadOnlyList<CodeDiagnostic> Items);
