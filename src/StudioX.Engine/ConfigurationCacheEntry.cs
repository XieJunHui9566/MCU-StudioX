namespace StudioX.Engine;

public sealed record ConfigurationCacheEntry(string RelativePath, bool Directory, long Bytes, int Files, string Stamp);
