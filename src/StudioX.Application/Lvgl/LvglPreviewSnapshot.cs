namespace StudioX.Application.Lvgl;

using StudioX.Engine.Lvgl;

public sealed record LvglPreviewSnapshot(string Project, string State, string Message, bool IsRunning,
    bool IsStale, string Log, LvglPreviewStats? Stats, LvglPreviewConfiguration? Configuration,
    int PointerBits = 0, string? LvglVersion = null, long DrawBufferBytes = 0);
