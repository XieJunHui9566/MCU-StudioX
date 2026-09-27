namespace StudioX.Engine;

public sealed record BuildMemoryTarget(string Name, string Configuration, DateTime BuiltAtUtc,
    IReadOnlyList<BuildMemoryRegion> Regions, string? Diagnostic = null);
