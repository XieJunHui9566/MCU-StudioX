namespace StudioX.Application.Distribution;

public sealed record DistributionListing(DistributionCatalog Catalog, string Source, string CatalogSha256, string Verification,
    bool BuiltInTrusted = false);
