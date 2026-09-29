namespace StudioX.Application.Editing;

/// <summary>后台发现文件，跳过产物和链接；文件名搜索不读取源码内容。</summary>
public sealed class WorkspaceDiscoveryService(ProjectFileService files)
{
    public Task<IReadOnlyList<string>> FilesAsync(string project, string query, CancellationToken token = default) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push("");
        var visited = 0;
        var excluded = new HashSet<string>([".git", ".studiox", ".build", "build", "bin", "obj", "node_modules", ".venv", "__pycache__"], StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 100000)
            {
                throw new InvalidOperationException("目录过多，请缩小工程范围。");
            }
            foreach (var entry in files.List(project, directory))
            {
                if (entry.IsLink)
                {
                    continue;
                }
                if (entry.IsDirectory)
                {
                    if (!excluded.Contains(entry.Name))
                    {
                        pending.Push(entry.RelativePath);
                    }
                    continue;
                }
                if (entry.RelativePath.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(entry.RelativePath);
                }
            }
        }
        return result.OrderBy(p => !Path.GetFileName(p).StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).Take(300).ToArray();
    }, token);
}
