namespace StudioX.Packages;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;

public static class PackArchiveWriter
{
    public static async Task WriteAsync(string sourceDirectory, string destination, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(sourceDirectory);
        var manifest = await JsonStore.ReadAsync<PackManifest>(Path.Combine(root, "manifest.json"), cancellationToken);
        PackValidator.Validate(manifest, root);
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        if (files.Contains("files.sha256.json")) throw new StudioXException("PACK_INDEX", "文件索引由打包器生成，源目录不应包含索引。");
        var output = Path.GetFullPath(destination);
        if (output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new StudioXException("PACK_OUTPUT", "输出包不能放进源目录。");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var relative in files)
                {
                    await using var source = File.OpenRead(PathBoundary.Resolve(root, relative));
                    hashes.Add(relative, Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken)).ToLowerInvariant());
                    source.Position = 0;
                    var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var target = entry.Open();
                    await source.CopyToAsync(target, cancellationToken);
                }
                var index = zip.CreateEntry("files.sha256.json", CompressionLevel.Optimal);
                index.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await using var indexStream = index.Open();
                await JsonSerializer.SerializeAsync(indexStream, hashes, JsonStore.Options, cancellationToken);
            }
            File.Move(temporary, output, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
