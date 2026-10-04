namespace StudioX.Packages;

using StudioX.Foundation;

/// <summary>包仓库与资源树的路径边界；列举前检查祖先，遍历时拒绝未索引的链接。</summary>
internal static class PackFileTree
{
    internal static void RejectLinkedAncestors(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PATH_LINK", "器件包目录及其父级不能是重解析点：" + directory.FullName);
            }
        }
    }

    internal static IReadOnlyList<string> EnumerateFiles(string root, CancellationToken token)
    {
        RejectLinkedAncestors(root);
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new StudioXException("PATH_LINK", "器件包资源树不能包含重解析点：" + entry);
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }
        return files;
    }
}
