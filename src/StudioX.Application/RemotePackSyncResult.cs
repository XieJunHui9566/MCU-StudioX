namespace StudioX.Application;

public sealed record RemotePackSyncResult(string CommitSha, int Imported, int Skipped,
    IReadOnlyList<RemotePackSyncFailure> Failures);
