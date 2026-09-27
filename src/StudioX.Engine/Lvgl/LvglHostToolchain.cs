namespace StudioX.Engine.Lvgl;

public sealed record LvglHostToolchain(string GccPath, string Target, string Version, string Sha256,
    string? ToolsetId = null, string? ToolsetVersion = null, string? Fingerprint = null, bool IsBundled = false);
