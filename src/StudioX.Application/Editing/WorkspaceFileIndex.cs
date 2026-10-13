namespace StudioX.Application.Editing;

/// <summary>单次快开会话的不可变文件名快照；筛选不再触碰磁盘，也不缓存文件内容。</summary>
public sealed class WorkspaceFileIndex
{
    private readonly string[] paths;
    private readonly HashSet<string> knownPaths;
    internal WorkspaceFileIndex(string[] paths)
    {
        this.paths = paths;
        knownPaths = new(paths, StringComparer.OrdinalIgnoreCase);
    }
    public int Count => paths.Length;
    internal bool Contains(string path) => knownPaths.Contains(path);
    public Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken token = default)
        => Task.Run<IReadOnlyList<string>>(() =>
        {
            var matches = new List<string>();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                if (path.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(path);
                }
            }
            var result = matches.OrderBy(path => !Path.GetFileName(path).StartsWith(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).Take(300).ToArray();
            token.ThrowIfCancellationRequested();
            return result;
        }, token);
}
