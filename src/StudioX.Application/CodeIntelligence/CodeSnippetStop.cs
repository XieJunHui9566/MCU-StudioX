namespace StudioX.Application.CodeIntelligence;

/// <summary>片段中的 UTF-16 可编辑范围；编号零表示结束位置。</summary>
public sealed record CodeSnippetStop(int Index, int Offset, int Length);
