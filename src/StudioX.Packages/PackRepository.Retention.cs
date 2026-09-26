namespace StudioX.Packages;

using StudioX.Foundation;

public sealed partial class PackRepository
{
    /// <summary>完整核验替代包后清理冗余版本；已建工程使用自己的 device 副本，不随全局清理升级。</summary>
    public Task<PackPruneResult> PruneSupersededAsync(CancellationToken token = default) => Task.Run(async () =>
    {
        await importGate.WaitAsync(token);
        try
        {
            if (!Directory.Exists(RootDirectory)) return new PackPruneResult([], []);
            RejectLinkedPackRoot();
            var catalog = await ListCatalogAsync(token);
            var retained = PackCatalogPolicy.SelectCurrentVersions(catalog);
            var removed = new List<RemovedPackVersion>();
            var failures = new List<PackPruneFailure>();
            var verified = new Dictionary<(string, string), InstalledPack>();
            foreach (var old in catalog.Except(retained))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var replacement = retained.First(pack => PackCatalogPolicy.Supersedes(pack.Manifest, old.Manifest));
                    var key = (replacement.Manifest.Id, replacement.Manifest.Version);
                    if (!verified.TryGetValue(key, out var current))
                    {
                        var replacementPath = PathBoundary.Resolve(RootDirectory, $"{key.Id}/{key.Version}");
                        current = await OpenAsync(replacementPath, token);
                        if (current.ContentHash != replacement.ContentHash)
                            throw new StudioXException("PACK_CHANGED", "清理前替代器件包已发生变化。");
                        verified.Add(key, current);
                    }
                    if (!PackCatalogPolicy.Supersedes(current.Manifest, old.Manifest))
                        throw new StudioXException("PACK_CHANGED", "替代器件包不再覆盖旧版器件和模板。");
                    var directory = PathBoundary.Resolve(RootDirectory, $"{old.Manifest.Id}/{old.Manifest.Version}");
                    if (!string.Equals(old.RootDirectory, Path.Combine(directory, "payload"), StringComparison.OrdinalIgnoreCase))
                        throw new StudioXException("PACK_INSTALLATION", "待清理器件包不在预期安装目录中。");
                    var oldEntry = await ReadCatalogEntryAsync(directory, token);
                    if (oldEntry.Pack.ContentHash != old.ContentHash)
                        throw new StudioXException("PACK_CHANGED", "清理前旧器件包已发生变化。");
                    var bytes = MeasurePackWithoutLinks(directory, token);
                    token.ThrowIfCancellationRequested();
                    var retired = PathBoundary.Resolve(RootDirectory, ".prune-" + Guid.NewGuid().ToString("N"));
                    // 先原子移出可选目录；被占用文件删除失败时，不让半个包破坏下次目录读取。
                    Directory.Move(directory, retired);
                    try
                    {
                        Directory.Delete(retired, recursive: true);
                        removed.Add(new(old.Manifest.Id, old.Manifest.Version, current.Manifest.Version, bytes));
                    }
                    catch (Exception ex)
                    {
                        removed.Add(new(old.Manifest.Id, old.Manifest.Version, current.Manifest.Version, 0));
                        failures.Add(new(old.Manifest.Id, old.Manifest.Version, "旧版已移出目录，但残留清理失败：" + retired + "：" + ex.Message));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) { failures.Add(new(old.Manifest.Id, old.Manifest.Version, ex.Message)); }
            }
            return new PackPruneResult(removed, failures);
        }
        finally { importGate.Release(); }
    }, token);

    private void RejectLinkedPackRoot()
    {
        // 只检查 root 下的相对路径不足以保护清理；父目录 junction 也会把整个仓库映射到外部目录。
        for (var directory = new DirectoryInfo(RootDirectory); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new StudioXException("PATH_LINK", "器件包清理目录及其父级不能是重解析点：" + directory.FullName);
    }

    private static long MeasurePackWithoutLinks(string directory, CancellationToken token)
    {
        var pending = new Stack<string>(); pending.Push(directory);
        long bytes = 0;
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new StudioXException("PATH_LINK", "待清理包包含重解析点：" + entry);
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else bytes = checked(bytes + new FileInfo(entry).Length);
            }
        }
        return bytes;
    }
}
