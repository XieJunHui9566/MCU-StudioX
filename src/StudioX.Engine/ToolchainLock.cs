namespace StudioX.Engine;

public sealed record ToolchainLock(int FormatVersion, string ToolsetId, string ToolsetVersion, string Fingerprint);
