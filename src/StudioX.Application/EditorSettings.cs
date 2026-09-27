namespace StudioX.Application;

public sealed record EditorSettings(int FormatVersion = 1, string FontFamily = "Cascadia Mono", double FontSize = 14, bool SoftGlow = true, double GlowStrength = 80);
