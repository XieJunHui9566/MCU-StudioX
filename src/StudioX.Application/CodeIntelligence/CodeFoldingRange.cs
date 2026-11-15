namespace StudioX.Application.CodeIntelligence;

/// <summary>仅隐藏括号内的正文，保留定义、起止括号与源码行号。</summary>
public sealed record CodeFoldingRange(int StartOffset, int EndOffset);
