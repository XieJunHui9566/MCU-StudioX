namespace StudioX.Application.CodeIntelligence;

public sealed record CodeDocumentSymbol(string Name, string Detail, int Kind, CodeRange Range,
    CodeRange SelectionRange, IReadOnlyList<CodeDocumentSymbol> Children);
