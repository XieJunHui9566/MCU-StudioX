namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using StudioX.Foundation;

public sealed partial class BuildService
{
    // 只处理已知的生成缓存和构建凭据；sdkconfig、固件、源码、工具锁及其他 .build 内容不在列表内。
    private static readonly string[] cacheFiles = [".build/CMakeCache.txt", ".build/CMakeFiles", ".build/.cmake",
        ".build/build.ninja", ".build/rules.ninja", ".build/compile_commands.json", ".build/studiox-idf-runtime.json", BuildReceipt.RelativePath];
    // IDF 6.x 响应文件和 specs 含绝对路径；保留旧文件会在新的 CMake 编译器探测阶段继续引用旧目录。
    internal static readonly string[] EspressifParameterCaches = [".build/toolchain", ".build/specs", ".build/bootloader/toolchain", ".build/bootloader/specs"];

    public Task<ConfigurationCachePlan> PreviewConfigurationResetAsync(string directory, CancellationToken token = default)
        => Task.Run(async () =>
        {
            var root = Path.GetFullPath(directory);
            var project = await ProjectService.ReadAsync(root, token);
            if (project.Kind is ProjectKind.MicroPython or ProjectKind.Zephyr)
            {
                throw new StudioXException("HEALTH_CACHE_UNAVAILABLE", "此工程不使用当前原生 CMake 构建入口。");
            }
            var selected = project.Espressif is null ? cacheFiles : cacheFiles.Concat(EspressifParameterCaches);
            var entries = selected.Select(relative => CacheEntry(root, relative, token)).OfType<ConfigurationCacheEntry>().ToArray();
            return new ConfigurationCachePlan(root, entries);
        }, token);

    public Task<string> ResetConfigurationCacheAsync(ConfigurationCachePlan plan, CancellationToken token = default)
        => Task.Run(async () =>
        {
            if (!await gate.WaitAsync(0, token))
            {
                throw new StudioXException("BUILD_BUSY", "构建期间不能修复配置缓存。");
            }
            var moved = new List<(string Source, string Backup, bool Directory)>();
            try
            {
                // 预览之后必须再次确认完整清单，防止新构建或外部修改让确认内容失效。
                var current = await PreviewConfigurationResetAsync(plan.ProjectDirectory, token);
                if (!current.Entries.SequenceEqual(plan.Entries))
                {
                    throw new StudioXException("HEALTH_CACHE_CHANGED", "配置缓存在预览后发生变化，请刷新检查并重新预览。");
                }
                if (current.Entries.Count == 0)
                {
                    throw new StudioXException("HEALTH_CACHE_EMPTY", "没有需要重建的配置缓存。");
                }
                var backup = PathBoundary.Resolve(current.ProjectDirectory, ".build/.studiox-cache-backups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);
                try
                {
                    foreach (var entry in current.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        var source = PathBoundary.Resolve(current.ProjectDirectory, entry.RelativePath);
                        var destination = PathBoundary.Resolve(backup, entry.RelativePath[".build/".Length..]);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        if (entry.Directory)
                        {
                            Directory.Move(source, destination);
                        }
                        else
                        {
                            File.Move(source, destination);
                        }
                        moved.Add((source, destination, entry.Directory));
                    }
                    await JsonStore.WriteAsync(PathBoundary.Resolve(backup, "cache-backup.json"), current, token);
                    return backup;
                }
                catch (Exception failure)
                {
                    var errors = new List<Exception> { failure };
                    foreach (var entry in moved.AsEnumerable().Reverse())
                    {
                        try
                        {
                            if (entry.Directory)
                            {
                                Directory.Move(entry.Backup, entry.Source);
                            }
                            else
                            {
                                File.Move(entry.Backup, entry.Source);
                            }
                        }
                        catch (Exception restoreError) { errors.Add(restoreError); }
                    }
                    if (errors.Count > 1)
                    {
                        throw new AggregateException("缓存修复失败，部分回退也失败；原文件保留在备份目录。", errors);
                    }
                    throw;
                }
            }
            finally { gate.Release(); }
        }, token);

    internal static ConfigurationCacheEntry? CacheEntry(string root, string relative, CancellationToken token)
    {
        var path = PathBoundary.Resolve(root, relative);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return null;
        }
        var isDirectory = Directory.Exists(path);
        var files = 0;
        long bytes = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var pending = new Stack<string>();
        pending.Push(relative);
        while (pending.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            var entryPath = PathBoundary.Resolve(root, next);
            var entry = new FileInfo(entryPath);
            if (Directory.Exists(entryPath))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(next + ":dir:" + Directory.GetLastWriteTimeUtc(entryPath).Ticks + "\n"));
                foreach (var child in Directory.EnumerateFileSystemEntries(entryPath).Order(StringComparer.Ordinal).Reverse())
                {
                    pending.Push(Path.GetRelativePath(root, child).Replace('\\', '/'));
                }
            }
            else
            {
                if (++files > 200000)
                {
                    throw new StudioXException("HEALTH_CACHE_SIZE", "缓存文件过多，停止预览并保留原目录。");
                }
                bytes += entry.Length;
                hash.AppendData(Encoding.UTF8.GetBytes(next + ":" + entry.Length + ":" + entry.LastWriteTimeUtc.Ticks + "\n"));
            }
        }
        return new(relative, isDirectory, bytes, files, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
