namespace StudioX.Application.CodeIntelligence;

public sealed record CodeLocation(string DocumentPath, CodeRange Range, string DisplayPath);
