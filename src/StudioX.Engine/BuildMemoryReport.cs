namespace StudioX.Engine;

public sealed record BuildMemoryReport(IReadOnlyList<BuildMemoryTarget> Targets, string Message,
    BuildLogicUsage? LogicUsage = null, string? LogicDiagnostic = null);
