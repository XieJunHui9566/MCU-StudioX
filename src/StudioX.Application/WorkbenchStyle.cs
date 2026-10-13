namespace StudioX.Application;

/// <summary>一次外观操作的配色与字体快照，保留编辑器其他偏好，不包含壁纸。</summary>
public sealed record WorkbenchStyle(ThemeDefinition Theme, TypographySettings Typography, EditorSettings Editor);
