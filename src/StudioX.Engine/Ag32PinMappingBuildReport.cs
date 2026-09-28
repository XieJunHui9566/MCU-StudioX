namespace StudioX.Engine;

public sealed record Ag32PinMappingBuildReport(bool Success, IReadOnlyList<string> Artifacts,
    string LogPath, string Log, int? ExitCode = null, bool TimedOut = false);
