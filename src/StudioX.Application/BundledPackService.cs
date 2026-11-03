namespace StudioX.Application;

using System.Security.Cryptography;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>分别导入普通器件包与 Zephyr 专用包，不把两种清单混入同一仓库。</summary>
public sealed class BundledPackService(PackRepository packs, ZephyrPackRepository zephyrPacks)
{
    public async Task<BundledPackImportResult> ImportAsync(string installationDirectory, CancellationToken token = default)
    {
        var root = Path.GetFullPath(installationDirectory);
        var ordinary = await packs.ImportBundledMissingAsync(Path.Combine(root, "device-packs"), token);
        var zephyr = await ImportZephyrAsync(Path.Combine(root, "zephyr-device-packs"), token);
        return new(ordinary.Imported + zephyr.Imported, ordinary.Skipped + zephyr.Skipped,
            [.. ordinary.Failures, .. zephyr.Failures]);
    }

    private async Task<BundledPackImportResult> ImportZephyrAsync(string root, CancellationToken token)
    {
        if (!Directory.Exists(root))
        {
            return new(0, 0, []);
        }
        var failures = new List<BundledPackImportFailure>();
        Entry[] entries;
        try
        {
            var index = PathBoundary.Resolve(root, "index.json");
            if (new FileInfo(index).Length > 2 * 1024 * 1024)
            {
                throw new StudioXException("ZEPHYR_BUNDLE_INDEX", "随附 Zephyr 器件包索引过大。");
            }
            entries = await JsonStore.ReadAsync<Entry[]>(index, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(0, 0, [new("zephyr-device-packs/index.json", ex.ToString())]); }

        var installed = await zephyrPacks.ListCatalogAsync(token);
        var known = installed.ToDictionary(pack => (pack.Manifest.Id, pack.Manifest.Version));
        var identities = new HashSet<(string, string)>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imported = 0;
        var skipped = 0;
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.File) || entry.Sha256 is not { Length: 64 } ||
                    !entry.Sha256.All(Uri.IsHexDigit) || !entry.File.EndsWith(".mcupack", StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("ZEPHYR_BUNDLE_INDEX", "随附 Zephyr 器件包索引字段无效。");
                }
                PackValidator.Token(entry.Id);
                PackValidator.Version(entry.Version);
                var archive = PathBoundary.Resolve(root, entry.File);
                if (!identities.Add((entry.Id, entry.Version)) || !paths.Add(archive))
                {
                    throw new StudioXException("ZEPHYR_BUNDLE_INDEX", "随附 Zephyr 器件包索引身份或路径重复。");
                }
                if (known.TryGetValue((entry.Id, entry.Version), out var existing))
                {
                    await ZephyrPackRepository.VerifyAsync(existing, token);
                    skipped++;
                    continue;
                }
                // 共享只读句柄覆盖哈希核对和解包，防止两步之间替换归档。
                await using var source = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, token));
                if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new StudioXException("ZEPHYR_BUNDLE_HASH", "随附 Zephyr 器件包哈希不匹配。");
                }
                using (var zip = new System.IO.Compression.ZipArchive(source, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true))
                {
                    var manifest = zip.GetEntry(ZephyrPackValidator.ManifestFile)
                        ?? throw new StudioXException("ZEPHYR_BUNDLE_ID", "随附 Zephyr 包缺少专用清单。");
                    if (manifest.Length > 2 * 1024 * 1024)
                    {
                        throw new StudioXException("ZEPHYR_BUNDLE_ID", "随附 Zephyr 包清单过大。");
                    }
                    await using var manifestStream = manifest.Open();
                    using var document = await System.Text.Json.JsonDocument.ParseAsync(manifestStream, cancellationToken: token);
                    if (document.RootElement.GetProperty("id").GetString() != entry.Id ||
                        document.RootElement.GetProperty("version").GetString() != entry.Version)
                    {
                        throw new StudioXException("ZEPHYR_BUNDLE_ID", "随附 Zephyr 包清单与目录身份不一致。");
                    }
                }
                var result = await zephyrPacks.ImportAsync(archive, token);
                known.Add((result.Manifest.Id, result.Manifest.Version), result);
                imported++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(new(entry?.File ?? "zephyr-device-packs/index.json", ex.ToString())); }
        }
        return new(imported, skipped, failures);
    }

    private sealed record Entry(string Id, string Version, string File, string Sha256);
}
