namespace StudioX.Application.CodeIntelligence;

public sealed record CodeDiagnostic(CodeRange Range, int Severity, string Message, string Source, string Code, System.Text.Json.JsonElement? Raw = null);
