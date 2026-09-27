namespace StudioX.Application.CodeIntelligence;

public sealed record CodeSignature(string Label, string Documentation, IReadOnlyList<string> Parameters, int ActiveParameter);
