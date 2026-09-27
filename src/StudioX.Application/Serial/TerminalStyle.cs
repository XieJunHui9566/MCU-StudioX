namespace StudioX.Application.Serial;

public sealed record TerminalStyle(int? Foreground = null, int? Background = null, bool Bold = false, bool Italic = false, bool Underline = false, bool Inverse = false);
