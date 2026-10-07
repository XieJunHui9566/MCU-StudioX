namespace StudioX.Application.CodeIntelligence;

/// <summary>编译器判定的未启用代码范围；只属于当前工程、文档文本和语言会话版本。</summary>
public sealed record CodeInactiveRegionBatch(string Project, string Path, string Text, int Version,
    IReadOnlyList<CodeRange> Regions);
