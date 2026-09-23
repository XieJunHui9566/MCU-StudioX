namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>一次目录枚举取得整棵树的元数据，避免每次构建重新读取数 GB 工具内容。</summary>
internal sealed class ToolsetSnapshot
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    public IEnumerable<string> Files => entries.Where(pair => !pair.Value.Attributes.HasFlag(FileAttributes.Directory)).Select(pair => pair.Key);

    public static ToolsetSnapshot Capture(string root, CancellationToken token)
    {
        var snapshot = new ToolsetSnapshot();
        var directories = new Stack<DirectoryInfo>();
        var rootInfo = new DirectoryInfo(root);
        snapshot.Add("", rootInfo); directories.Push(rootInfo);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        while (directories.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var item in directory.EnumerateFileSystemInfos("*", options))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, item.FullName).Replace('\\', '/');
                snapshot.Add(relative, item);
                if (item is DirectoryInfo child) directories.Push(child);
            }
        }
        return snapshot;
    }

    private void Add(string path, FileSystemInfo info)
    {
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new StudioXException("PATH_LINK", "工具目录不能包含符号链接或重解析点：" + path);
        // Windows 在复制大量文件后可能延迟刷新目录的时间戳。目录本身的时间不能
        // 证明文件内容变化；路径集合、重解析点检查和文件元数据仍完整参与快照。
        var entry = info is FileInfo file
            ? new Entry(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks, file.Attributes)
            : new Entry(0, 0, 0, info.Attributes);
        entries.Add(path, entry);
    }

    public bool Matches(ToolsetSnapshot other) => entries.Count == other.entries.Count &&
        entries.All(pair => other.entries.TryGetValue(pair.Key, out var entry) && entry == pair.Value);

    public string? FirstDifference(ToolsetSnapshot other)
    {
        foreach (var (path, entry) in entries)
            if (!other.entries.TryGetValue(path, out var current) || current != entry) return path;
        return other.entries.Keys.FirstOrDefault(path => !entries.ContainsKey(path));
    }

    private readonly record struct Entry(long Length, long LastWriteUtcTicks, long CreationUtcTicks, FileAttributes Attributes);
}
