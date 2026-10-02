namespace StudioX.Application.Distribution;

public sealed record DistributionSpacePlan(string Directory, long RequiredBytes, long AvailableBytes);
