namespace StudioX.Application;

public sealed record RemotePackSyncProgress(int Total, int Processed, string? CurrentPath,
    string Stage, int Imported, int Skipped, int Failed);
