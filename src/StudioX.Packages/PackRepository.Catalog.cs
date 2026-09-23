namespace StudioX.Packages;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;

public sealed partial class PackRepository
{
    /// <summary>用于选择器的轻量目录；只读取安装记录、索引与清单，不遍历 SDK。创建工程前必须完整校验选中的包。</summary>
    public Task<IReadOnlyList<InstalledPack>> ListCatalogAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(RootDirectory)) return (IReadOnlyList<InstalledPack>)Array.Empty<InstalledPack>();
        var result = new List<InstalledPack>();
        foreach (var id in Directory.EnumerateDirectories(RootDirectory).Where(p => !Path.GetFileName(p).StartsWith('.')))
            foreach (var version in Directory.EnumerateDirectories(id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add((await ReadCatalogEntryAsync(version, cancellationToken)).Pack);
            }
        return result.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal).ThenBy(p => p.Manifest.Version, StringComparer.Ordinal).ToArray();
    }, cancellationToken);

    /// <summary>重新读取并校验选中的安装包，拒绝选择后被替换的内容；不把轻量目录条目当作完整性凭据。</summary>
    public static Task<InstalledPack> VerifyAsync(InstalledPack selected, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(selected.RootDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(root)!;
        if (!string.Equals(root, Path.Combine(directory, "payload"), StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("PACK_INSTALLATION", "芯片包不在有效的安装目录中。");
        var verified = await OpenAsync(directory, cancellationToken);
        if (verified.ContentHash != selected.ContentHash || verified.Manifest.Id != selected.Manifest.Id || verified.Manifest.Version != selected.Manifest.Version)
            throw new StudioXException("PACK_CHANGED", "选择后芯片包发生变化，请重新选择器件包。");
        return verified;
    }, cancellationToken);

    private static async Task<(InstalledPack Pack, Dictionary<string, string> Hashes)> ReadCatalogEntryAsync(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var install = await JsonStore.ReadAsync<Installation>(PathBoundary.Resolve(directory, "installation.json"), token);
        if (install.FormatVersion != 1) throw new StudioXException("PACK_INSTALLATION", "安装记录格式不支持。");
        var hashes = await JsonStore.ReadAsync<Dictionary<string, string>>(PathBoundary.Resolve(directory, HashIndex), token);
        if (CalculateContentHash(hashes) != install.ContentHash || !hashes.TryGetValue("manifest.json", out var manifestHash))
            throw new StudioXException("PACK_HASH", "已安装芯片包的内容索引发生变化。");
        var root = PathBoundary.Resolve(directory, "payload");
        var bytes = await File.ReadAllBytesAsync(PathBoundary.Resolve(root, "manifest.json"), token);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(manifestHash, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("PACK_HASH", "已安装芯片包发生变化：manifest.json");
        var manifest = JsonSerializer.Deserialize<PackManifest>(bytes, JsonStore.Options)
            ?? throw new StudioXException("PACK_MANIFEST", "芯片包清单为空。");
        // 列举仅验证目录身份和显示所需数据；资源路径、模板和 SDK 文件在实际使用前完整检查。
        PackValidator.Token(manifest.Id); PackValidator.Version(manifest.Version);
        if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.DisplayName) ||
            string.IsNullOrWhiteSpace(manifest.Vendor) || manifest.Devices is not { Count: > 0 } ||
            Path.GetFileName(directory) != manifest.Version || Path.GetFileName(Path.GetDirectoryName(directory)) != manifest.Id)
            throw new StudioXException("PACK_MANIFEST", "芯片包清单与安装目录不一致或缺少器件信息。");
        return (new InstalledPack(manifest, root, install.ContentHash), hashes);
    }
}
