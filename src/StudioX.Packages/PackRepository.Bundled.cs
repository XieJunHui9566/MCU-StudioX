namespace StudioX.Packages;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Foundation;

public sealed record BundledPackImportFailure(string File, string Message);
public sealed record BundledPackImportResult(int Imported, int Skipped, IReadOnlyList<BundledPackImportFailure> Failures);

public sealed partial class PackRepository
{
    /// <summary>安装发行版缺少的包；先处理新版本，避免重新导入已由新包完整覆盖的旧版本。</summary>
    public async Task<BundledPackImportResult> ImportBundledMissingAsync(string bundledDirectory, CancellationToken cancellationToken = default)
    {
        var bundledRoot = Path.GetFullPath(bundledDirectory);
        if (!Directory.Exists(bundledRoot)) return new(0, 0, []);
        var failures = new List<BundledPackImportFailure>();
        var indexPath = PathBoundary.Resolve(bundledRoot, "index.json");
        if (!File.Exists(indexPath) || new FileInfo(indexPath).Length > 2 * 1024 * 1024)
            return new(0, 0, [new("index.json", "发行版器件包目录缺少有效的索引。")]);

        BundledPackIndexEntry[] entries;
        try { entries = await JsonStore.ReadAsync<BundledPackIndexEntry[]>(indexPath, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new(0, 0, [new("index.json", ex.Message)]); }

        var installed = (await ListCatalogAsync(cancellationToken)).ToList();
        var known = installed.Select(pack => (pack.Manifest.Id, pack.Manifest.Version)).ToHashSet();
        var verifiedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var damagedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIdentities = new HashSet<(string, string)>();
        var validEntries = new List<BundledPackIndexEntry>();
        var imported = 0;
        var skipped = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry?.File ?? "(index entry)";
            try
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Version) ||
                    string.IsNullOrWhiteSpace(entry.Sha256) || string.IsNullOrWhiteSpace(entry.File))
                    throw new StudioXException("PACK_BUNDLE_INDEX", "发行版器件包索引缺少必需字段。");
                PackValidator.Token(entry.Id);
                PackValidator.Version(entry.Version);
                if (!entry.File.EndsWith(".mcupack", StringComparison.OrdinalIgnoreCase) ||
                    entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit) ||
                    !seenPaths.Add(entry.File) || !seenIdentities.Add((entry.Id, entry.Version)))
                    throw new StudioXException("PACK_BUNDLE_INDEX", "发行版器件包索引中的路径、哈希或身份无效或重复。");
                _ = PathBoundary.Resolve(bundledRoot, entry.File);
                validEntries.Add(entry);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(new(name, ex.Message)); }
        }

        // 索引顺序不是版本顺序；先安装新版才能判断旧版是否还提供独有器件或模板。
        foreach (var entry in validEntries.OrderByDescending(entry => entry.Version!, Comparer<string>.Create(PackVersion.Compare)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var archive = PathBoundary.Resolve(bundledRoot, entry.File!);
                if (known.Contains((entry.Id!, entry.Version!))) { skipped++; continue; }

                // 持有只读句柄，Windows 上可防止哈希核对与 ImportAsync 之间替换压缩包。
                await using var source = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
                if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new StudioXException("PACK_BUNDLE_HASH", "发行版器件包压缩文件的 SHA-256 与索引不一致。");
                source.Position = 0;
                PackManifest manifest;
                using (var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true))
                {
                    var manifestEntry = zip.GetEntry("manifest.json")
                        ?? throw new StudioXException("PACK_BUNDLE_MANIFEST", "器件包缺少清单。");
                    if (manifestEntry.Length > 2 * 1024 * 1024)
                        throw new StudioXException("PACK_BUNDLE_MANIFEST", "器件包清单过大。");
                    await using var manifestStream = manifestEntry.Open();
                    manifest = await JsonSerializer.DeserializeAsync<PackManifest>(manifestStream, JsonStore.Options, cancellationToken)
                        ?? throw new StudioXException("PACK_BUNDLE_MANIFEST", "器件包清单无效。");
                    if (manifest.Id != entry.Id || manifest.Version != entry.Version)
                        throw new StudioXException("PACK_BUNDLE_ID", "发行版索引与器件包清单的 ID/版本不一致。");
                }
                var superseded = false;
                foreach (var candidate in installed.Where(pack => PackCatalogPolicy.Supersedes(pack.Manifest, manifest)))
                {
                    if (damagedRoots.Contains(candidate.RootDirectory)) continue;
                    if (!verifiedRoots.Contains(candidate.RootDirectory))
                    {
                        try
                        {
                            await VerifyAsync(candidate, cancellationToken);
                            verifiedRoots.Add(candidate.RootDirectory);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            // 损坏的新包不能阻止可用旧包恢复；保留原始错误，且每轮只报告一次。
                            damagedRoots.Add(candidate.RootDirectory);
                            failures.Add(new($"{candidate.Manifest.Id}/{candidate.Manifest.Version}（已安装）", ex.Message));
                            continue;
                        }
                    }
                    superseded = true;
                    break;
                }
                if (superseded)
                {
                    skipped++;
                    continue;
                }
                var pack = await ImportAsync(archive, cancellationToken);
                known.Add((pack.Manifest.Id, pack.Manifest.Version));
                installed.Add(pack);
                verifiedRoots.Add(pack.RootDirectory);
                imported++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add(new(entry.File!, ex.Message)); }
        }
        return new(imported, skipped, failures);
    }

    private sealed record BundledPackIndexEntry(string? File, string? Id, string? Version, string? Sha256);
}
