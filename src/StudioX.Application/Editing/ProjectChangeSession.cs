namespace StudioX.Application.Editing;

using StudioX.Foundation;

/// <summary>合并重复磁盘通知，限制积压并报告丢失；停止后不再发布旧工程回调。</summary>
public sealed class ProjectChangeSession : IDisposable
{
    private readonly object gate = new();
    private readonly FileSystemWatcher watcher;
    private readonly Timer timer;
    private readonly Dictionary<string, ProjectFileChange> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProjectFileChange> renames = [];
    private bool rescan, disposed;
    private string? diagnostic;
    private long revision;
    private DateTimeOffset? firstPending;
    private readonly ProjectFileIdentityTracker identities;
    private bool identityFailureReported;
    public string Directory
    {
        get;
    }
    public event Action<ProjectChangeBatch>? Changed;

    internal ProjectChangeSession(string directory)
    {
        Directory = Path.GetFullPath(directory);
        identities = new(Directory);
        for (var current = new DirectoryInfo(Directory); current is not null; current = current.Parent)
        {
            if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new StudioXException("PROJECT_WATCH_PATH", "工程监听目录不存在或经过链接。");
            }
        }
        timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(Directory)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes
        };
        watcher.Changed += (_, args) => Notify(new(Relative(args.FullPath)));
        watcher.Created += (_, args) => Notify(new(Relative(args.FullPath), ProjectFileChangeKind.Created));
        watcher.Deleted += (_, args) => Notify(new(Relative(args.FullPath), ProjectFileChangeKind.Deleted));
        watcher.Renamed += (_, args) => Notify(new(Relative(args.FullPath), ProjectFileChangeKind.Renamed, Relative(args.OldFullPath)));
        watcher.Error += (_, args) => RequestRescan(args.GetException().ToString());
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher.Dispose();
            timer.Dispose();
            throw;
        }
    }

    private string Relative(string path) => Path.GetRelativePath(Directory, path).Replace('\\', '/');

    public void TrackPath(string path)
    {
        lock (gate)
        {
            if (disposed || path.Length == 0)
            {
                return;
            }
            try
            {
                identities.Track(path);
                // 打开文档的父目录也可能整体移动，即使它尚未在工程树中展开。
                for (var parent = ProjectFileService.ParentDirectory(path); parent.Length > 0; parent = ProjectFileService.ParentDirectory(parent))
                {
                    identities.Track(parent);
                }
            }
            catch (Exception error)
            {
                if (!identityFailureReported)
                {
                    identityFailureReported = true;
                    RequestRescan(error.ToString());
                }
            }
        }
    }

    public void UntrackPath(string path)
    {
        lock (gate)
        {
            identities.Untrack(path);
        }
    }

    /// <summary>应用内写入也可进入同一批次；原生监听仍兜底捕获其它工具的变化。</summary>
    public void Notify(ProjectFileChange change)
    {
        var observesNew = Safe(change.Path) && ProjectChangePolicy.Observes(change.Path);
        var observesOld = change.PreviousPath is { } previous && Safe(previous) && ProjectChangePolicy.Observes(previous);
        if (!observesNew && !observesOld)
        {
            return;
        }
        // 原子保存还会改变父目录的时间戳；它不是目录内容身份变化，不能撤销刚完成的构建。
        if (change.Kind == ProjectFileChangeKind.Changed && System.IO.Directory.Exists(Path.Combine(Directory, change.Path)))
        {
            return;
        }
        if (change.Kind == ProjectFileChangeKind.Renamed && observesNew != observesOld)
        {
            change = observesNew ? new(change.Path, ProjectFileChangeKind.Created) : new(change.PreviousPath!, ProjectFileChangeKind.Deleted);
        }
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            if (pending.Count + renames.Count >= 2048)
            {
                rescan = true;
                diagnostic = "工程变化队列超过 2,048 项，已合并为完整重新核对。";
                pending.Clear();
                renames.Clear();
            }
            if (!rescan)
            {
                if (change.Kind == ProjectFileChangeKind.Renamed)
                {
                    // 改名链必须保留顺序；仅按最终路径合并会丢失打开文档的原身份。
                    renames.Add(change);
                }
                else if (!pending.TryGetValue(change.Path, out var existing) || change.Kind != ProjectFileChangeKind.Changed)
                {
                    pending[change.Path] = change;
                }
                else
                {
                    pending[change.Path] = existing;
                }
            }
            Schedule();
        }
    }

    private static bool Safe(string path) => path.Length > 0 && !Path.IsPathRooted(path) && !path.Split('/').Any(part => part is ".." or ".");

    public void RequestRescan(string? error = null)
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            rescan = true;
            diagnostic = error;
            Schedule();
        }
    }

    private void Schedule()
    {
        firstPending ??= DateTimeOffset.UtcNow;
        // 连续生成文件不能无限延后同步；最长等待 750 ms 后发布当前有界批次。
        var remaining = 750 - (DateTimeOffset.UtcNow - firstPending.Value).TotalMilliseconds;
        timer.Change((int)Math.Clamp(remaining, 0, 180), Timeout.Infinite);
    }

    private void Flush()
    {
        lock (gate)
        {
            if (disposed || !rescan && pending.Count == 0 && renames.Count == 0)
            {
                return;
            }
            IReadOnlyList<ProjectFileChange> changes = renames.Concat(pending.Values).ToArray();
            try
            {
                changes = identities.Normalize(changes);
            }
            catch (Exception error)
            {
                diagnostic = error.ToString();
                rescan = true;
            }
            var batch = new ProjectChangeBatch(Directory, ++revision, changes, rescan, diagnostic);
            pending.Clear();
            renames.Clear();
            rescan = false;
            diagnostic = null;
            firstPending = null;
            // 订阅者只应排队；持锁发布确保 Dispose 返回后不会出现新的回调。
            Changed?.Invoke(batch);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            watcher.Dispose();
            timer.Dispose();
            identities.Dispose();
            pending.Clear();
            renames.Clear();
            Changed = null;
        }
    }
}
