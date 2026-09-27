namespace StudioX.Application;

public sealed record BuildDiagnostic(string RelativePath, int Line, int Column, bool IsWarning, string Message);
