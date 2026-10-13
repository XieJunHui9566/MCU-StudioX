namespace StudioX.Extensions.Abstractions;

/// <summary>格式 1 提供配色，格式 2 增加包内插画；字体仍由宿主提供。</summary>
public sealed record PluginAppearanceDefinition(int FormatVersion, PluginAppearanceTheme[] Themes);
