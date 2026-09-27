namespace StudioX.Application.CodeIntelligence;

public sealed record CodeSuggestion(string Label, string InsertText, string FilterText, string Detail,
    string Documentation, int Kind, CodeRange? Range, string SortText);
