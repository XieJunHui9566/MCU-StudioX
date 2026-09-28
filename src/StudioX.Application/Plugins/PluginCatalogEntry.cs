namespace StudioX.Application.Plugins;

using StudioX.Extensions;

/// <summary>已发现的插件与针对当前内容的显式运行授权；无效清单仍保留诊断。</summary>
public sealed record PluginCatalogEntry(
    string Id,
    PluginManifest? Manifest,
    string ManifestPath,
    bool IsBundled,
    bool Enabled,
    bool CanEnable,
    string? Diagnostic)
{
    internal string? ContentFingerprint { get; init; }
    public string DisplayName => Manifest?.DisplayName ?? Id;
    public string Version => Manifest?.Version ?? "未知";
    public string[] Capabilities => Manifest?.Capabilities ?? [];
}
