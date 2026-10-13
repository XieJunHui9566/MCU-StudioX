namespace StudioX.Application.Editing;

using StudioX.Foundation;

/// <summary>仅跟踪打开文档与已加载目录，按文件身份快照还原 Windows 的删除/创建移动通知。</summary>
internal sealed class ProjectFileIdentityTracker(string directory) : IDisposable
{
    private readonly Dictionary<string, WindowsFileIdentity> tracked = new(StringComparer.OrdinalIgnoreCase);

    public void Track(string path)
    {
        var candidate = WindowsFileIdentity.Read(PathBoundary.Resolve(directory, path));
        if (candidate is null || tracked.TryGetValue(path, out var current) && current.Value == candidate.Value)
        {
            return;
        }
        if (tracked.Count >= 2048 && !tracked.ContainsKey(path))
        {
            throw new StudioXException("PROJECT_IDENTITY_LIMIT", "工程身份跟踪超过 2,048 项；未跟踪对象仍按增删同步并保留编辑内容。");
        }
        tracked[path] = candidate;
    }

    public void Untrack(string path)
    {
        tracked.Remove(path);
    }

    public IReadOnlyList<ProjectFileChange> Normalize(IReadOnlyList<ProjectFileChange> changes)
    {
        var result = changes.ToList();
        foreach (var rename in changes.Where(change => change.Kind == ProjectFileChangeKind.Renamed))
        {
            Remap(rename.PreviousPath!, rename.Path);
        }
        foreach (var created in changes.Where(change => change.Kind == ProjectFileChangeKind.Created))
        {
            var current = WindowsFileIdentity.Read(PathBoundary.Resolve(directory, created.Path));
            if (current is null)
            {
                continue;
            }
            var matches = tracked.Where(pair => pair.Value.Value == current.Value && !pair.Key.Equals(created.Path, StringComparison.OrdinalIgnoreCase) &&
                !File.Exists(PathBoundary.Resolve(directory, pair.Key)) && !Directory.Exists(PathBoundary.Resolve(directory, pair.Key))).Select(pair => pair.Key).ToArray();
            if (matches.Length != 1)
            {
                continue;
            }
            var previous = matches[0];
            result.RemoveAll(change => change.Kind == ProjectFileChangeKind.Deleted && ProjectFileService.ContainsPath(previous, change.Path));
            result.Remove(created);
            result.Add(new(created.Path, ProjectFileChangeKind.Renamed, previous));
            Remap(previous, created.Path);
        }
        return result;
    }

    private void Remap(string previous, string next)
    {
        foreach (var (path, identity) in tracked.Where(pair => ProjectFileService.ContainsPath(previous, pair.Key)).ToArray())
        {
            tracked.Remove(path);
            var mapped = next + path[previous.Length..];
            Untrack(mapped);
            tracked[mapped] = identity;
        }
    }

    public void Dispose()
    {
        tracked.Clear();
    }
}
