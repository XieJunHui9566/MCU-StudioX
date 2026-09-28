namespace StudioX.Packages;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>从 Zephyr 专用清单和资源生成带 SHA-256 索引的 .mcupack。</summary>
public static class ZephyrPackArchiveWriter
{
    public static async Task WriteAsync(string sourceDirectory, string destination, CancellationToken token = default)
    {
        var root = Path.GetFullPath(sourceDirectory);
        ZephyrPackFileTree.RejectLinkedAncestors(root);
        var manifestPath = PathBoundary.Resolve(root, ZephyrPackValidator.ManifestFile);
        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await ZephyrPackJson.ReadAsync<ZephyrPackManifest>(manifestStream, token);
        ZephyrPackValidator.Validate(manifest, root);
        var files = ZephyrPackFileTree.Enumerate(root, token);
        if (files.Count is < 1 or > 9999 || files.Contains(ZephyrPackValidator.HashIndex, StringComparer.OrdinalIgnoreCase) ||
            files.Contains("manifest.json", StringComparer.OrdinalIgnoreCase))
        {
            throw new StudioXException("ZEPHYR_PACK_FORMAT", "Zephyr 包源目录包含保留文件或超出文件数量限制。");
        }
        var output = Path.GetFullPath(destination);
        if (!output.EndsWith(".mcupack", StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("ZEPHYR_PACK_OUTPUT", "Zephyr 包输出必须是源目录外的 .mcupack 文件。");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
                long total = 0;
                foreach (var relative in files)
                {
                    token.ThrowIfCancellationRequested();
                    var path = PathBoundary.Resolve(root, relative);
                    await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (source.Length > 64 * 1024 * 1024 ||
                        relative == ZephyrPackValidator.ManifestFile && source.Length > 2 * 1024 * 1024)
                    {
                        throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包源文件超出大小限制：" + relative);
                    }
                    var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var target = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[65536];
                    int count;
                    long written = 0;
                    while ((count = await source.ReadAsync(buffer, token)) > 0)
                    {
                        written += count;
                        total += count;
                        if (written > 64 * 1024 * 1024 || total > 512 * 1024 * 1024)
                        {
                            throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包总量超出限制。");
                        }
                        hash.AppendData(buffer, 0, count);
                        await target.WriteAsync(buffer.AsMemory(0, count), token);
                    }
                    hashes.Add(relative, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
                }
                await using var indexBuffer = new MemoryStream();
                await JsonSerializer.SerializeAsync(indexBuffer, hashes, JsonStore.Options, token);
                if (indexBuffer.Length > 2 * 1024 * 1024 || total + indexBuffer.Length > 512 * 1024 * 1024)
                {
                    throw new StudioXException("ZEPHYR_PACK_LIMIT", "Zephyr 包哈希索引或总量超出限制。");
                }
                var index = zip.CreateEntry(ZephyrPackValidator.HashIndex, CompressionLevel.Optimal);
                index.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var indexStream = index.Open();
                indexBuffer.Position = 0;
                await indexBuffer.CopyToAsync(indexStream, token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, output, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
