namespace StudioX.Packages;

public sealed record ZephyrPackCatalogReport(IReadOnlyList<InstalledZephyrPack> Packs,
    IReadOnlyList<ZephyrPackCatalogFailure> Failures);
