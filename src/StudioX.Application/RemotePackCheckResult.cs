namespace StudioX.Application;

public sealed record RemotePackCheckResult(string CommitSha, int UpToDate,
    IReadOnlyList<RemotePackUpdate> Updates);
