namespace StudioX.Application.Distribution;

public sealed record DistributionCatalog(int FormatVersion, string Publisher, DistributionEntry[] Entries);
