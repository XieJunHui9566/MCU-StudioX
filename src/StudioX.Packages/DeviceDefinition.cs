namespace StudioX.Packages;

public sealed record DeviceDefinition(string Id, string DisplayName, string Architecture,
    uint FlashOrigin, uint FlashBytes, uint RamOrigin, uint RamBytes,
    string ToolsetId, string ToolsetVersion, string CompilerId,
    IReadOnlyList<string> CpuFlags, IReadOnlyList<string> Defines,
    IReadOnlyList<string> IncludeDirectories, IReadOnlyList<string> Sources,
    string LinkerScript, IReadOnlyList<string> CompileOptions, IReadOnlyList<string> LinkOptions,
    IReadOnlyList<ProjectTemplate> Templates, OpenOcdDefinition? OpenOcd = null);

public sealed record OpenOcdDefinition(string TargetScript, IReadOnlyList<DebugProbeDefinition> Probes, uint? ApplicationFlashBytes = null);
public sealed record DebugProbeDefinition(string Id, string DisplayName, string InterfaceScript, string Transport, int DefaultSpeedKhz);
