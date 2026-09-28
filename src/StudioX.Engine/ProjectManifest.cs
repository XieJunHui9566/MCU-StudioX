namespace StudioX.Engine;

public sealed record ProjectManifest(int FormatVersion, string Name, string PackId, string PackVersion, string PackContentHash,
    string DeviceId, string TemplateId, string ToolsetId, string ToolsetVersion, string CompilerId,
    ProjectKind Kind = ProjectKind.Pack, CubeMxProjectSettings? CubeMx = null, Ag32LogicProjectSettings? Logic = null,
    EspressifProjectSettings? Espressif = null, string? EntryFile = null, Ag32PinMappingProjectSettings? PinMapping = null,
    ZephyrProjectSettings? Zephyr = null, StudioX.Packages.MicroPythonProfile? MicroPython = null);
