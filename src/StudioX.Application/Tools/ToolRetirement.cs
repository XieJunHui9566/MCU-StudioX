namespace StudioX.Application.Tools;

public sealed record ToolRetirement(int FormatVersion, string Id, string Version, string CompilerId, string Fingerprint,
    DateTimeOffset RetiredUtc, string State = "retired");
