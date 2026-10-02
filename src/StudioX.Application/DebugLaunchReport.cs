namespace StudioX.Application;

public sealed record DebugLaunchReport(DateTimeOffset StartedAtUtc, bool Busy, IReadOnlyList<DebugLaunchStep> Steps,
    string? LogPath = null, string? Diagnostic = null);
