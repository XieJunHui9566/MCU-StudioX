namespace StudioX.Packages;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;

/// <summary>Zephyr 专用包仓库；同扩展名不意味着沿用裸机清单或安装目录。</summary>
public sealed class ZephyrPackRepository(string rootDirectory)
{
    private const long MaximumFileBytes = 64 * 1024 * 1024;
    private const long MaximumPackBytes = 512 * 1024 * 1024;
    private const long MaximumMetadataBytes = 2 * 1024 * 1024;
    private const int MaximumEntries = 10000;
    private readonly SemaphoreSlim importGate = new(1, 1);

    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    /// <summary>完整校验归档后原子发布；相同 ID/版本的不同内容拒绝覆盖。</summary>
    public async Task<InstalledZephyrPack> ImportAsync(string archivePath, CancellationToken token = default)
    {
        if (!archivePath.EndsWith(".mcupack", StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("ZEPHYR_PACK_FORMAT", "Zephyr 包必须使用 .mcupack 扩展名。");
        }
        await importGate.WaitAsync(token);
        var staging = "";
        try
        {
            ZephyrPackFileTree.RejectLinkedAncestors(RootDirectory);
            Directory.CreateDirectory(RootDirectory);
            staging = PathBoundary.Resolve(RootDirectory, ".import-" + Guid.NewGuid().ToString("N"));
            var payload = Directory.CreateDirectory(Path.Combine(staging, "payload")).FullName;
            await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count is < 2 or > MaximumEntries)
            {
                throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包文件数量超出限制。");
            }
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/'))
                {
                    throw new StudioXException("ZEPHYR_PACK_PATH", "Zephyr 包不接受 ZIP 目录条目。");
                }
                _ = PathBoundary.Resolve(payload, entry.FullName);
                if (!paths.Add(entry.FullName))
                {
                    throw new StudioXException("ZEPHYR_PACK_PATH", "Zephyr 包路径重复或大小写冲突。");
                }
                if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000)
                {
                    throw new StudioXException("ZEPHYR_PACK_LINK", "Zephyr 包不接受符号链接。");
                }
                total = checked(total + entry.Length);
                if (entry.Length > MaximumFileBytes || total > MaximumPackBytes)
                {
                    throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包大小超出限制。");
                }
            }
            if (paths.Contains("manifest.json"))
            {
                throw new StudioXException("ZEPHYR_PACK_FORMAT", "Zephyr 包不能包含普通芯片包清单 manifest.json。");
            }
            var indexEntry = zip.GetEntry(ZephyrPackValidator.HashIndex);
            var manifestEntry = zip.GetEntry(ZephyrPackValidator.ManifestFile);
            if (indexEntry is null || manifestEntry is null)
            {
                throw new StudioXException("ZEPHYR_PACK_FORMAT", "Zephyr 包缺少专用清单或 SHA-256 索引。");
            }
            if (indexEntry.Length > MaximumMetadataBytes || manifestEntry.Length > MaximumMetadataBytes)
            {
                throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包清单或哈希索引过大。");
            }
            Dictionary<string, string> hashes;
            await using (var indexStream = indexEntry.Open())
            {
                hashes = await ZephyrPackJson.ReadAsync<Dictionary<string, string>>(indexStream, token);
            }
            ValidateIndex(hashes, payload);
            if (hashes.Count != zip.Entries.Count - 1)
            {
                throw new StudioXException("ZEPHYR_PACK_INDEX", "Zephyr 包索引与归档文件集合不一致。");
            }
            foreach (var entry in zip.Entries.Where(item => item.FullName != ZephyrPackValidator.HashIndex))
            {
                token.ThrowIfCancellationRequested();
                if (!hashes.TryGetValue(entry.FullName, out var expected))
                {
                    throw new StudioXException("ZEPHYR_PACK_INDEX", "Zephyr 包索引缺少文件：" + entry.FullName);
                }
                var target = PathBoundary.Resolve(payload, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open();
                await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token)) > 0)
                {
                    written += count;
                    if (written > entry.Length || written > MaximumFileBytes)
                    {
                        throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包解压数据超过声明长度。");
                    }
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (written != entry.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("ZEPHYR_PACK_HASH", "Zephyr 包文件校验失败：" + entry.FullName);
                }
            }
            var manifest = await ReadManifestAsync(PathBoundary.Resolve(payload, ZephyrPackValidator.ManifestFile), token);
            ZephyrPackValidator.Validate(manifest, payload);
            var contentHash = CalculateContentHash(hashes);
            var finalDirectory = PathBoundary.Resolve(RootDirectory, manifest.Id + "/" + manifest.Version);
            if (Directory.Exists(finalDirectory))
            {
                var installed = await OpenAsync(finalDirectory, token);
                if (installed.ContentHash != contentHash)
                {
                    throw new StudioXException("ZEPHYR_PACK_VERSION_CONFLICT", "同 ID 和版本已有不同内容，不能覆盖。");
                }
                return installed;
            }
            await JsonStore.WriteAsync(Path.Combine(staging, "installation.json"), new Installation(1, contentHash), token);
            await JsonStore.WriteAsync(Path.Combine(staging, ZephyrPackValidator.HashIndex), hashes, token);
            Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, finalDirectory);
            staging = "";
            return new InstalledZephyrPack(manifest, Path.Combine(finalDirectory, "payload"), contentHash);
        }
        finally
        {
            if (staging.Length > 0 && Directory.Exists(staging))
            {
                ZephyrPackFileTree.RejectLinkedAncestors(staging);
                Directory.Delete(staging, recursive: true);
            }
            importGate.Release();
        }
    }

    /// <summary>选择器目录只检查安装身份、索引摘要和清单；使用前调用 VerifyAsync。</summary>
    public async Task<IReadOnlyList<InstalledZephyrPack>> ListCatalogAsync(CancellationToken token = default) =>
        (await ListCatalogReportAsync(token)).Packs;

    /// <summary>逐项读取目录，坏包的原始诊断随报告返回，不阻止选择健康的包。</summary>
    public Task<ZephyrPackCatalogReport> ListCatalogReportAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        if (!Directory.Exists(RootDirectory))
        {
            return new ZephyrPackCatalogReport([], []);
        }
        ZephyrPackFileTree.RejectLinkedAncestors(RootDirectory);
        var result = new List<InstalledZephyrPack>();
        var failures = new List<ZephyrPackCatalogFailure>();
        foreach (var id in Directory.EnumerateDirectories(RootDirectory).Where(path => !Path.GetFileName(path).StartsWith('.')))
        {
            string[] versions;
            try { versions = Directory.GetDirectories(id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StudioXException)
            {
                failures.Add(new(id, ex.ToString()));
                continue;
            }
            foreach (var version in versions)
            {
                token.ThrowIfCancellationRequested();
                try { result.Add((await ReadCatalogEntryAsync(version, token)).Pack); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or StudioXException)
                {
                    failures.Add(new(version, ex.ToString()));
                }
            }
        }
        return new ZephyrPackCatalogReport(result.OrderBy(pack => pack.Manifest.Id, StringComparer.Ordinal)
            .ThenBy(pack => pack.Manifest.Version, StringComparer.Ordinal).ToArray(), failures);
    }, token);

    /// <summary>对仓库中的所有包执行完整文件集合、哈希和清单校验。</summary>
    public Task<IReadOnlyList<InstalledZephyrPack>> ListAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        if (!Directory.Exists(RootDirectory))
        {
            return (IReadOnlyList<InstalledZephyrPack>)[];
        }
        ZephyrPackFileTree.RejectLinkedAncestors(RootDirectory);
        var result = new List<InstalledZephyrPack>();
        foreach (var id in Directory.EnumerateDirectories(RootDirectory).Where(path => !Path.GetFileName(path).StartsWith('.')))
        {
            foreach (var version in Directory.EnumerateDirectories(id))
            {
                token.ThrowIfCancellationRequested();
                result.Add(await OpenAsync(version, token));
            }
        }
        return (IReadOnlyList<InstalledZephyrPack>)result.OrderBy(pack => pack.Manifest.Id, StringComparer.Ordinal)
            .ThenBy(pack => pack.Manifest.Version, StringComparer.Ordinal).ToArray();
    }, token);

    /// <summary>重新完整校验被选中的包，拒绝选择后替换的内容。</summary>
    public static Task<InstalledZephyrPack> VerifyAsync(InstalledZephyrPack selected, CancellationToken token = default) => Task.Run(async () =>
    {
        var root = Path.GetFullPath(selected.RootDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(root)!;
        ZephyrPackFileTree.RejectLinkedAncestors(directory);
        if (!root.Equals(Path.Combine(directory, "payload"), StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("ZEPHYR_PACK_INSTALLATION", "Zephyr 包不在有效的安装目录中。");
        }
        var verified = await OpenAsync(directory, token);
        if (verified.ContentHash != selected.ContentHash || verified.Manifest.Id != selected.Manifest.Id ||
            verified.Manifest.Version != selected.Manifest.Version)
        {
            throw new StudioXException("ZEPHYR_PACK_CHANGED", "选择后 Zephyr 包发生变化，请重新选择。");
        }
        return verified;
    }, token);

    private static async Task<InstalledZephyrPack> OpenAsync(string directory, CancellationToken token)
    {
        var (pack, hashes) = await ReadCatalogEntryAsync(directory, token);
        var files = ZephyrPackFileTree.Enumerate(pack.RootDirectory, token);
        if (!files.ToHashSet(StringComparer.Ordinal).SetEquals(hashes.Keys))
        {
            throw new StudioXException("ZEPHYR_PACK_HASH", "已安装 Zephyr 包的文件集合与索引不一致。");
        }
        long total = 0;
        foreach (var (relative, expected) in hashes)
        {
            token.ThrowIfCancellationRequested();
            await using var source = File.OpenRead(PathBoundary.Resolve(pack.RootDirectory, relative));
            total = checked(total + source.Length);
            if (source.Length > MaximumFileBytes || total > MaximumPackBytes)
            {
                throw new StudioXException("ZEPHYR_PACK_LIMIT", "已安装 Zephyr 包超出大小限制。");
            }
            if (!Convert.ToHexString(await SHA256.HashDataAsync(source, token)).Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("ZEPHYR_PACK_HASH", "已安装 Zephyr 包文件发生变化：" + relative);
            }
        }
        ZephyrPackValidator.Validate(pack.Manifest, pack.RootDirectory);
        return pack;
    }

    private static async Task<(InstalledZephyrPack Pack, Dictionary<string, string> Hashes)> ReadCatalogEntryAsync(string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ZephyrPackFileTree.RejectLinkedAncestors(directory);
        var installPath = PathBoundary.Resolve(directory, "installation.json");
        if (new FileInfo(installPath).Length > MaximumMetadataBytes)
        {
            throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包安装记录过大。");
        }
        await using var installStream = File.OpenRead(installPath);
        var installation = await ZephyrPackJson.ReadAsync<Installation>(installStream, token);
        if (installation.FormatVersion != 1 || !ValidHash(installation.ContentHash))
        {
            throw new StudioXException("ZEPHYR_PACK_INSTALLATION", "Zephyr 包安装记录无效。");
        }
        var indexPath = PathBoundary.Resolve(directory, ZephyrPackValidator.HashIndex);
        if (new FileInfo(indexPath).Length > MaximumMetadataBytes)
        {
            throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包安装索引过大。");
        }
        await using var indexStream = File.OpenRead(indexPath);
        var hashes = await ZephyrPackJson.ReadAsync<Dictionary<string, string>>(indexStream, token);
        var root = PathBoundary.Resolve(directory, "payload");
        ValidateIndex(hashes, root);
        if (!CalculateContentHash(hashes).Equals(installation.ContentHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("ZEPHYR_PACK_HASH", "Zephyr 包安装索引发生变化。");
        }
        var manifestPath = PathBoundary.Resolve(root, ZephyrPackValidator.ManifestFile);
        if (new FileInfo(manifestPath).Length > MaximumMetadataBytes)
        {
            throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包安装清单过大。");
        }
        var bytes = await File.ReadAllBytesAsync(manifestPath, token);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(hashes[ZephyrPackValidator.ManifestFile], StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("ZEPHYR_PACK_HASH", "Zephyr 包清单发生变化。");
        }
        await using var manifestStream = new MemoryStream(bytes, writable: false);
        var manifest = await ZephyrPackJson.ReadAsync<ZephyrPackManifest>(manifestStream, token);
        ZephyrPackValidator.ValidateCatalog(manifest, root);
        if (!string.Equals(Path.GetFileName(directory), manifest.Version, StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), manifest.Id, StringComparison.Ordinal))
        {
            throw new StudioXException("ZEPHYR_PACK_INSTALLATION", "Zephyr 包清单身份与安装目录不一致。");
        }
        return (new InstalledZephyrPack(manifest, root, installation.ContentHash), hashes);
    }

    private static async Task<ZephyrPackManifest> ReadManifestAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return await ZephyrPackJson.ReadAsync<ZephyrPackManifest>(stream, token);
    }

    private static void ValidateIndex(Dictionary<string, string> hashes, string root)
    {
        if (hashes.Count is < 1 or > MaximumEntries || !hashes.ContainsKey(ZephyrPackValidator.ManifestFile) ||
            hashes.ContainsKey(ZephyrPackValidator.HashIndex) || hashes.ContainsKey("manifest.json"))
        {
            throw new StudioXException("ZEPHYR_PACK_INDEX", "Zephyr 包索引缺少专用清单或包含保留文件。");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, value) in hashes)
        {
            _ = PathBoundary.Resolve(root, relative);
            if (!paths.Add(relative) || !ValidHash(value))
            {
                throw new StudioXException("ZEPHYR_PACK_INDEX", "Zephyr 包索引路径重复或 SHA-256 无效。");
            }
        }
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string CalculateContentHash(Dictionary<string, string> hashes)
    {
        var canonical = string.Join('\n', hashes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Key + "\t" + pair.Value.ToLowerInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private sealed record Installation(int FormatVersion, string ContentHash);
}
