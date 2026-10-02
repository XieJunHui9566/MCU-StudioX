namespace StudioX.Engine;

public sealed record ConfigurationCachePlan(string ProjectDirectory, IReadOnlyList<ConfigurationCacheEntry> Entries)
{
    public long Bytes => Entries.Sum(entry => entry.Bytes);
}
