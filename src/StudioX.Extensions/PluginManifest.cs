namespace StudioX.Extensions;

using System.Security.Cryptography;
using StudioX.Foundation;
using StudioX.Packages;

public sealed record PluginManifest(int FormatVersion, int ApiVersion, string Id, string Version, string DisplayName,
    string EntryAssembly, string EntryType, string[] Capabilities, Dictionary<string, string> Sha256)
{
    public static async Task<PluginManifest> ReadAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var manifest = await JsonStore.ReadAsync<PluginManifest>(manifestPath, cancellationToken);
        if (manifest.FormatVersion != 1 || manifest.ApiVersion != 1) throw new StudioXException("PLUGIN_API", "插件 API 或清单版本不兼容。");
        PackValidator.Token(manifest.Id); PackValidator.Version(manifest.Version);
        if (manifest.Capabilities is not ["decode"] || string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.Sha256 is null)
            throw new StudioXException("PLUGIN_MANIFEST", "首版只支持 decode 能力，且必须指定入口类型和文件索引。");
        var root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var entry = PathBoundary.Resolve(root, manifest.EntryAssembly);
        if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !manifest.Sha256.ContainsKey(manifest.EntryAssembly))
            throw new StudioXException("PLUGIN_ENTRY", "插件入口必须是受索引保护的 .NET 程序集。");
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.Equals(Path.GetFullPath(manifestPath), StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        if (!files.SetEquals(manifest.Sha256.Keys)) throw new StudioXException("PLUGIN_HASH", "插件目录与文件索引不一致。");
        foreach (var (relative, expected) in manifest.Sha256)
        {
            await using var source = File.OpenRead(PathBoundary.Resolve(root, relative));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new StudioXException("PLUGIN_HASH", $"插件文件校验失败：{relative}");
        }
        return manifest;
    }
}
