namespace StudioX.Packages;

public sealed record PackPruneResult(IReadOnlyList<RemovedPackVersion> Removed, IReadOnlyList<PackPruneFailure> Failures)
{
    public long ReclaimedBytes => Removed.Sum(pack => pack.Bytes);
}
