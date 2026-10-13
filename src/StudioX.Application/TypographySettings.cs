namespace StudioX.Application;

/// <summary>工作台界面字体偏好，独立于编辑器字体及背景媒体。</summary>
public sealed record TypographySettings(int FormatVersion = 1, string FontFamily = "Segoe UI, Microsoft YaHei UI", double FontSize = 13);
