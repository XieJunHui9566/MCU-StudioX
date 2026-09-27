namespace StudioX.Packages;

public sealed record OpenOcdDefinition(string TargetScript, IReadOnlyList<DebugProbeDefinition> Probes, uint? ApplicationFlashBytes = null);
