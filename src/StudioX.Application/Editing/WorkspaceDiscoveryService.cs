namespace StudioX.Application.Editing;

/// <summary>后台发现文件，跳过产物和链接；文件名搜索不读取源码内容。</summary>
public sealed class WorkspaceDiscoveryService(ProjectFileService files)
{
    public async Task<IReadOnlyList<string>> FilesAsync(string project, string query, CancellationToken token = default)
        => await (await CreateIndexAsync(project, token).ConfigureAwait(false)).SearchAsync(query, token).ConfigureAwait(false);

    /// <summary>快开窗口复用一次完整发现结果；重新打开窗口时重新发现，避免长期缓存漏掉外部修改。</summary>
    public Task<WorkspaceFileIndex> CreateIndexAsync(string project, CancellationToken token = default) => Task.Run(() =>
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push("");
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 100000)
            {
                throw new InvalidOperationException("目录过多，请缩小工程范围。");
            }
            foreach (var entry in files.Enumerate(project, directory, token))
            {
                if (entry.IsLink)
                {
                    continue;
                }
                if (entry.IsDirectory)
                {
                    if (!ProjectChangePolicy.ExcludesDirectory(entry.Name))
                    {
                        pending.Push(entry.RelativePath);
                    }
                    continue;
                }
                if (result.Count >= 100000)
                {
                    throw new InvalidOperationException("快速打开最多发现 100,000 个文件，请缩小工程范围。");
                }
                result.Add(entry.RelativePath);
            }
        }
        return new WorkspaceFileIndex(result.ToArray());
    }, token);
}
