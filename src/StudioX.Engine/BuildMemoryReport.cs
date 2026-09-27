namespace StudioX.Engine;

public sealed record BuildMemoryReport(IReadOnlyList<BuildMemoryTarget> Targets, string Message);
