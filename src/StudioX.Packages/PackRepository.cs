namespace StudioX.Packages;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>完整哈希验证后原子导入；不执行芯片包中的任何代码。</summary>
public sealed partial class PackRepository(string rootDirectory)
{
    private const string HashIndex = "files.sha256.json";
    private const long MaximumFileBytes = 64 * 1024 * 1024;
    private const long MaximumPackBytes = 512 * 1024 * 1024;
    private readonly SemaphoreSlim importGate = new(1, 1);
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    public async Task<InstalledPack> ImportAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        await importGate.WaitAsync(cancellationToken);
        var staging = Path.Combine(RootDirectory, ".import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            var payload = Directory.CreateDirectory(Path.Combine(staging, "payload")).FullName;
            await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count is < 2 or > 10000) throw new StudioXException("PACK_LIMIT", "芯片包文件数量超出限制。");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                PathBoundary.Resolve(payload, entry.FullName);
                if (!paths.Add(entry.FullName)) throw new StudioXException("PACK_DUPLICATE_PATH", "芯片包路径重复或大小写冲突。");
                if ((entry.ExternalAttributes >> 16 & 0xf000) == 0xa000) throw new StudioXException("PACK_LINK", "芯片包不接受符号链接。");
                total = checked(total + entry.Length);
                if (entry.Length > MaximumFileBytes || total > MaximumPackBytes) throw new StudioXException("PACK_LIMIT", "芯片包大小超出限制。");
            }
            var indexEntry = zip.GetEntry(HashIndex) ?? throw new StudioXException("PACK_FORMAT", "缺少 StudioX 格式 1 哈希索引；旧芯片包不兼容。");
            if (indexEntry.Length > 2 * 1024 * 1024) throw new StudioXException("PACK_LIMIT", "哈希索引过大。");
            Dictionary<string, string> hashes;
            await using (var indexStream = indexEntry.Open())
                hashes = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(indexStream, JsonStore.Options, cancellationToken)
                    ?? throw new StudioXException("PACK_INDEX", "哈希索引无效。");
            if (hashes.Count != zip.Entries.Count - 1 || !hashes.ContainsKey("manifest.json") || hashes.ContainsKey(HashIndex))
                throw new StudioXException("PACK_INDEX", "哈希索引与文件集合不一致。");
            foreach (var entry in zip.Entries.Where(e => e.FullName != HashIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!hashes.TryGetValue(entry.FullName, out var expected) || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
                    throw new StudioXException("PACK_INDEX", $"缺少有效文件哈希：{entry.FullName}");
                var target = PathBoundary.Resolve(payload, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open();
                await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += count;
                    if (written > entry.Length || written > MaximumFileBytes) throw new StudioXException("PACK_LIMIT", "解压数据超过声明长度。");
                    hash.AppendData(buffer, 0, count);
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                if (written != entry.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new StudioXException("PACK_HASH", $"文件校验失败：{entry.FullName}");
            }
            var manifest = await JsonStore.ReadAsync<PackManifest>(Path.Combine(payload, "manifest.json"), cancellationToken);
            PackValidator.Validate(manifest, payload);
            var contentHash = CalculateContentHash(hashes);
            var finalDirectory = PathBoundary.Resolve(RootDirectory, $"{manifest.Id}/{manifest.Version}");
            if (Directory.Exists(finalDirectory))
            {
                var installed = await OpenAsync(finalDirectory, cancellationToken);
                if (installed.ContentHash != contentHash) throw new StudioXException("PACK_VERSION_CONFLICT", "同 ID 和版本已有不同内容，不能覆盖。");
                return installed;
            }
            await JsonStore.WriteAsync(Path.Combine(staging, "installation.json"), new Installation(1, contentHash), cancellationToken);
            await JsonStore.WriteAsync(Path.Combine(staging, HashIndex), hashes, cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
            Directory.Move(staging, finalDirectory);
            return new InstalledPack(manifest, Path.Combine(finalDirectory, "payload"), contentHash);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            importGate.Release();
        }
    }

    public async Task<IReadOnlyList<InstalledPack>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return [];
        var result = new List<InstalledPack>();
        foreach (var id in Directory.EnumerateDirectories(RootDirectory).Where(p => !Path.GetFileName(p).StartsWith('.')))
            foreach (var version in Directory.EnumerateDirectories(id))
                result.Add(await OpenAsync(version, cancellationToken));
        return result.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal).ThenBy(p => p.Manifest.Version, StringComparer.Ordinal).ToArray();
    }

    private static async Task<InstalledPack> OpenAsync(string directory, CancellationToken cancellationToken)
    {
        var (pack, hashes) = await ReadCatalogEntryAsync(directory, cancellationToken);
        var root = pack.RootDirectory;
        if (Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length != hashes.Count)
            throw new StudioXException("PACK_HASH", "已安装芯片包的内容索引发生变化。");
        foreach (var (relative, expected) in hashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = File.OpenRead(PathBoundary.Resolve(root, relative));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new StudioXException("PACK_HASH", $"已安装芯片包发生变化：{relative}");
        }
        PackValidator.Validate(pack.Manifest, root);
        return pack;
    }

    private static string CalculateContentHash(Dictionary<string, string> hashes)
    {
        var canonical = string.Join('\n', hashes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}\t{p.Value.ToLowerInvariant()}"));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
    private sealed record Installation(int FormatVersion, string ContentHash);
}
