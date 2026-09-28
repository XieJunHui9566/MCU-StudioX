namespace StudioX.Application.CodeIntelligence;

/// <summary>Python 离线编辑提示；不代表解释器或类型检查器的分析结果。</summary>
public sealed record PythonAssistance(IReadOnlyList<CodeSuggestion> Suggestions, CodeSignature? Signature);
