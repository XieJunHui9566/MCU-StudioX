namespace StudioX.Extensions;

using System.IO.Compression;
using System.Security.Cryptography;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>校验并原子安装插件归档；安装阶段不加载入口程序集。</summary>
public sealed class PluginRepository
{
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const long MaximumTotalBytes = 256L * 1024 * 1024;
    private readonly string root;

    public PluginRepository(string userPluginsDirectory)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userPluginsDirectory));
    }

    public async Task<IReadOnlyList<InstalledPlugin>> ListAsync(CancellationToken token = default)
    {
        CheckAncestors(root);
        if (!Directory.Exists(root))
        {
            return [];
        }
        List<InstalledPlugin> result = [];
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(directory).StartsWith('.'))
            {
                continue;
            }
            var manifestPath = PathBoundary.Resolve(root, Path.GetFileName(directory) + "/plugin.json");
            var manifest = await PluginManifest.ReadAsync(manifestPath, token).ConfigureAwait(false);
            if (manifest.Id != Path.GetFileName(directory))
            {
                throw new StudioXException("PLUGIN_ID", "安装目录与插件 ID 不一致。");
            }
            result.Add(new(directory, manifestPath, manifest));
        }
        return result;
    }

    public Task<InstalledPlugin> ImportAsync(string archive, CancellationToken token = default) =>
        ImportAsync(archive, null, token);

    public async Task<InstalledPlugin> ImportAsync(string archive,
        Func<PluginManifest, CancellationToken, Task>? beforePublish, CancellationToken token = default, bool allowDowngrade = false)
    {
        CheckAncestors(root);
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            await ExtractAsync(archive, staging, token).ConfigureAwait(false);
            var manifest = await PluginManifest.ReadAsync(Path.Combine(staging, "plugin.json"), token).ConfigureAwait(false);
            await using var repositoryLock = await LockAsync(token).ConfigureAwait(false);
            var destination = PathBoundary.Resolve(root, manifest.Id);
            var manifestPath = Path.Combine(destination, "plugin.json");
            if (Directory.Exists(destination))
            {
                var current = await PluginManifest.ReadAsync(manifestPath, token).ConfigureAwait(false);
                if (current.Id != manifest.Id)
                {
                    throw new StudioXException("PLUGIN_ID", "已有安装目录与插件 ID 不一致。");
                }
                var comparison = PackVersion.Compare(manifest.Version, current.Version);
                // 只有应用服务校验用户选择的回退归档后才可放行降级；内容校验与原子替换仍完全执行。
                if (comparison < 0 && !allowDowngrade)
                {
                    throw new StudioXException("PLUGIN_DOWNGRADE", "拒绝以旧版本覆盖插件。");
                }
                if (comparison == 0)
                {
                    if (await HashAsync(manifestPath, token).ConfigureAwait(false) !=
                        await HashAsync(Path.Combine(staging, "plugin.json"), token).ConfigureAwait(false))
                    {
                        throw new StudioXException("PLUGIN_VERSION_CONFLICT", "同版本插件的内容不同，请更新插件版本。");
                    }
                    return new(destination, manifestPath, current);
                }
            }
            if (beforePublish is not null)
            {
                await beforePublish(manifest, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            var backup = Path.Combine(root, ".replace-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(destination))
            {
                CheckTree(destination);
                await DirectoryMoves.MoveAsync(destination, backup, token).ConfigureAwait(false);
            }
            try
            {
                await DirectoryMoves.MoveAsync(staging, destination, token).ConfigureAwait(false);
            }
            catch
            {
                if (Directory.Exists(backup))
                {
                    // 已撤走旧目录时，即使用户取消也必须先恢复原版本。
                    await DirectoryMoves.MoveAsync(backup, destination, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
            DeleteOwned(backup);
            return new(destination, manifestPath, manifest);
        }
        finally
        {
            DeleteOwned(staging);
        }
    }

    public async Task UninstallAsync(string id, CancellationToken token = default)
    {
        PackValidator.Token(id);
        CheckAncestors(root);
        if (!Directory.Exists(root))
        {
            return;
        }
        await using var repositoryLock = await LockAsync(token).ConfigureAwait(false);
        DeleteOwned(PathBoundary.Resolve(root, id));
    }

    /// <summary>从发布目录生成完整哈希索引；不修改开发者的源清单。</summary>
    public static async Task PackAsync(string sourceDirectory, string archivePath, CancellationToken token = default)
    {
        var sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
        CheckAncestors(sourceRoot);
        CheckTree(sourceRoot);
        var output = Path.GetFullPath(archivePath);
        if (!output.EndsWith(".studioxplugin", StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("PLUGIN_OUTPUT", "输出须为源目录之外的 .studioxplugin 文件。");
        }
        CheckAncestors(Path.GetDirectoryName(output)!);
        var repository = new PluginRepository(Path.Combine(Path.GetTempPath(), "StudioX-plugin-pack-" + Guid.NewGuid().ToString("N")));
        var staging = Path.Combine(repository.root, ".pack");
        Directory.CreateDirectory(staging);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(sourceRoot, path).Replace('\\', '/'))
                .Order(StringComparer.Ordinal).ToArray();
            if (files.Length is < 2 or > 4096)
            {
                throw new StudioXException("PLUGIN_LIMIT", "插件发布目录必须包含清单和有界文件集。");
            }
            Dictionary<string, string> hashes = new(StringComparer.Ordinal);
            long total = 0;
            foreach (var relative in files)
            {
                if (relative == "plugin.json")
                {
                    continue;
                }
                var source = PathBoundary.Resolve(sourceRoot, relative);
                total = checked(total + new FileInfo(source).Length);
                if (new FileInfo(source).Length > MaximumFileBytes || total > MaximumTotalBytes)
                {
                    throw new StudioXException("PLUGIN_LIMIT", "发布文件超过插件容量限制。");
                }
                var target = PathBoundary.Resolve(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = File.OpenRead(source))
                {
                    await using (var destination = new FileStream(target, FileMode.CreateNew))
                    {
                        await input.CopyToAsync(destination, token).ConfigureAwait(false);
                    }
                }
                hashes.Add(relative, await HashAsync(target, token).ConfigureAwait(false));
            }
            var manifest = await JsonStore.ReadAsync<PluginManifest>(Path.Combine(sourceRoot, "plugin.json"), token).ConfigureAwait(false);
            await JsonStore.WriteAsync(Path.Combine(staging, "plugin.json"), manifest with
            {
                Sha256 = hashes
            }, token).ConfigureAwait(false);
            _ = await PluginManifest.ReadAsync(Path.Combine(staging, "plugin.json"), token).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew))
            {
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var relative in hashes.Keys.Append("plugin.json").Order(StringComparer.Ordinal))
                {
                    var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    await using var input = File.OpenRead(PathBoundary.Resolve(staging, relative));
                    await using var target = entry.Open();
                    await input.CopyToAsync(target, token).ConfigureAwait(false);
                }
            }
            File.Move(temporary, output, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
            repository.DeleteOwned(staging);
            Directory.Delete(repository.root);
        }
    }

    private static async Task ExtractAsync(string archive, string destination, CancellationToken token)
    {
        if (!archive.EndsWith(".studioxplugin", StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("PLUGIN_ARCHIVE", "插件归档扩展名必须为 .studioxplugin。");
        }
        await using var file = File.OpenRead(archive);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 2 or > 4096)
        {
            throw new StudioXException("PLUGIN_ARCHIVE", "插件归档文件数量无效。");
        }
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var path = PathBoundary.Resolve(destination, entry.FullName);
            // Windows 的大小写不敏感和 ZIP 的 Unix 链接属性都须在写文件前检查。
            if (!paths.Add(entry.FullName) || (entry.ExternalAttributes >> 16 & 0xf000) is 0xa000 or 0x4000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PLUGIN_ARCHIVE", "归档含重复路径、目录项或链接。");
            }
            total = checked(total + entry.Length);
            if (entry.Length > MaximumFileBytes || total > MaximumTotalBytes ||
                (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 500))
            {
                throw new StudioXException("PLUGIN_LIMIT", "插件超过解压大小限制。");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[65536];
            long written = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                written += count;
                if (written > entry.Length)
                {
                    throw new StudioXException("PLUGIN_ARCHIVE", "实际数据超过 ZIP 声明长度。");
                }
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (written != entry.Length)
            {
                throw new StudioXException("PLUGIN_ARCHIVE", "实际数据与 ZIP 声明长度不一致。");
            }
        }
    }

    private async Task<FileStream> LockAsync(CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, ".repository.lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }

    private void DeleteOwned(string directory)
    {
        CheckAncestors(root);
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("PLUGIN_PATH", "清理路径超出插件仓库。");
        }
        if (Directory.Exists(full))
        {
            _ = PathBoundary.Resolve(root, Path.GetRelativePath(root, full).Replace('\\', '/'));
            CheckTree(full);
            Directory.Delete(full, recursive: true);
        }
    }

    private static void CheckTree(string directory)
    {
        CheckAncestors(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PLUGIN_LINK", "插件目录不能包含链接。");
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                CheckTree(entry);
            }
        }
    }

    private static void CheckAncestors(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PLUGIN_LINK", "插件存储路径不能经过链接目录。");
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}
