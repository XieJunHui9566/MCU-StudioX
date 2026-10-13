namespace StudioX.Extensions.Abstractions;

/// <summary>插件包内受哈希校验的 PNG 插画；不接受远程资源或可执行标记。</summary>
public sealed record PluginAppearanceArtwork(string Asset, string Sha256, double Opacity = .75);
