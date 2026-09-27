namespace StudioX.Engine.Lvgl;

public sealed record LvglDiagnostic(string Code, string Severity, string Message, string? Path = null);
