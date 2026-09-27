namespace StudioX.Application.Lvgl;

/// <summary>输入作用于当前预览进程，不向操作系统全局注入按键。</summary>
public sealed record LvglPreviewInput(string Type, int X = 0, int Y = 0, bool Pressed = false,
    int Delta = 0, int Key = 0);
