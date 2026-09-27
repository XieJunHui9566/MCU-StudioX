namespace StudioX.Engine;

public sealed record ToolsetManifest(int FormatVersion, string Id, string Version, string Host, string CompilerId,
    Dictionary<string, string> Executables, Dictionary<string, string> Sha256, string? DisplayName = null,
    Dictionary<string, string>? ComponentVersions = null, Dictionary<string, string>? ResourceDirectories = null,
    string? Purpose = null);
