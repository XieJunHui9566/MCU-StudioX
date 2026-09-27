namespace StudioX.Application.CodeIntelligence;

public sealed record CodeHover(string Contents, IReadOnlyList<CodeLocation> Declarations);
