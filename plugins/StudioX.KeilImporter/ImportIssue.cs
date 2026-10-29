namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record ImportIssue(string Severity, string Code, string Message);
