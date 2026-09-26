namespace StudioX.Packages;

public sealed record RemovedPackVersion(string Id, string Version, string ReplacementVersion, long Bytes);
public sealed record PackPruneFailure(string Id, string Version, string Message);
public sealed record PackPruneResult(IReadOnlyList<RemovedPackVersion> Removed, IReadOnlyList<PackPruneFailure> Failures)
{
    public long ReclaimedBytes => Removed.Sum(pack => pack.Bytes);
}
