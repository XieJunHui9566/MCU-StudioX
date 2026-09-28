namespace StudioX.Packages;

/// <summary>已安装 Zephyr 包的清单、只读资源根目录及完整内容指纹。</summary>
public sealed record InstalledZephyrPack(ZephyrPackManifest Manifest, string RootDirectory, string ContentHash);
