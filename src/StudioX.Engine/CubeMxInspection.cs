namespace StudioX.Engine;

public sealed record CubeMxInspection(string Directory, string Name, string Device, string IocFile,
    string ToolchainFile, IReadOnlyList<string> ConfigurePresets);
