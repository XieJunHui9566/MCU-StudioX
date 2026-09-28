namespace StudioX.Extensions;

/// <summary>经过完整文件哈希验证的本机安装条目。</summary>
public sealed record InstalledPlugin(string Directory, string ManifestPath, PluginManifest Manifest);
