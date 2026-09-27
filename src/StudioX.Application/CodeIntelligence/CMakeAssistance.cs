namespace StudioX.Application.CodeIntelligence;

public sealed record CMakeAssistance(IReadOnlyList<CodeSuggestion> Suggestions, CodeSignature? Signature);
