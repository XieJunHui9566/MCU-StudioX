namespace StudioX.Application.Serial;

public sealed record TerminalSnapshot(long Version, string Text, IReadOnlyList<TerminalSpan> Spans, long TrimmedLines);
