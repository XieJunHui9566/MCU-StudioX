namespace StudioX.Engine;

public sealed record CubeMxProjectSettings(string IocFile, string ToolchainFile, string? ConfigurePreset, string BuildType = "Debug");
