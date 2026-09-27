namespace StudioX.Engine.Lvgl;

public sealed record LvglPreviewBuildResult(bool Success, string Executable, string BuildDirectory,
    string Log, LvglPreviewConfiguration Configuration, string? RuntimeResourcesDirectory = null);
