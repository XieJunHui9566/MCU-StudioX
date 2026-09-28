namespace StudioX.Packages;

using StudioX.Foundation;

/// <summary>列举包树时拒绝重解析点，完整核验时不会跟随目录链接。</summary>
internal static class ZephyrPackFileTree
{
    internal static IReadOnlyList<string> Enumerate(string root, CancellationToken token)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new StudioXException("ZEPHYR_PACK_PATH", "Zephyr 包资源根目录不存在或是重解析点。");
        }
        var pending = new Stack<DirectoryInfo>();
        pending.Push(directory);
        var files = new List<string>();
        while (pending.TryPop(out var current))
        {
            foreach (var entry in current.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("ZEPHYR_PACK_LINK", "Zephyr 包不接受符号链接或重解析点：" + entry.FullName);
                }
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
                else if (entry is FileInfo)
                {
                    var relative = Path.GetRelativePath(directory.FullName, entry.FullName).Replace('\\', '/');
                    _ = PathBoundary.Resolve(directory.FullName, relative);
                    files.Add(relative);
                }
                else
                {
                    throw new StudioXException("ZEPHYR_PACK_PATH", "Zephyr 包包含未知类型的文件系统条目。");
                }
            }
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    internal static void RejectLinkedAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("ZEPHYR_PACK_LINK", "Zephyr 包仓库路径不能经过重解析点：" + directory.FullName);
            }
        }
    }
}
