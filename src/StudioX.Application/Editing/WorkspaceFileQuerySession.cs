namespace StudioX.Application.Editing;

/// <summary>快开会话共享一次文件发现；结构变化废弃旧扫描，查询取消不影响其它查询。</summary>
public sealed class WorkspaceFileQuerySession(WorkspaceDiscoveryService discovery, string project) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly List<(Task<WorkspaceFileIndex> Task, CancellationTokenSource Cancellation)> scans = [];
    private (Task<WorkspaceFileIndex> Task, CancellationTokenSource Cancellation)? current;
    private bool disposed;

    public bool Invalidate(ProjectChangeBatch batch)
    {
        lock (gate)
        {
            if (disposed || !batch.NamesChanged)
            {
                return false;
            }
            var changes = batch.Changes.Where(change => ProjectChangePolicy.IsDiscoverable(change.Path) ||
                change.PreviousPath is { } previous && ProjectChangePolicy.IsDiscoverable(previous)).ToArray();
            if (!batch.RequiresRescan && (changes.Length == 0 || current is { } scan && scan.Task.IsCompletedSuccessfully &&
                changes.All(change => change.Kind == ProjectFileChangeKind.Created && scan.Task.Result.Contains(change.Path))))
            {
                return false;
            }
            Invalidate();
            return true;
        }
    }

    public void Invalidate()
    {
        lock (gate)
        {
            current?.Cancellation.Cancel();
            current = null;
        }
    }

    public async Task<IReadOnlyList<string>> SearchAsync(string query, CancellationToken token = default)
    {
        while (true)
        {
            (Task<WorkspaceFileIndex> Task, CancellationTokenSource Cancellation) scan;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (current is null)
                {
                    foreach (var old in scans.Where(item => item.Task.IsCompletedSuccessfully || item.Task.IsCanceled).ToArray())
                    {
                        old.Cancellation.Dispose();
                        scans.Remove(old);
                    }
                    var cancellation = new CancellationTokenSource();
                    current = (discovery.CreateIndexAsync(project, cancellation.Token), cancellation);
                    scans.Add(current.Value);
                }
                scan = current.Value;
            }
            try
            {
                var index = await scan.Task.WaitAsync(token).ConfigureAwait(false);
                var result = await index.SearchAsync(query, token).ConfigureAwait(false);
                lock (gate)
                {
                    if (current?.Task == scan.Task)
                    {
                        return result;
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && scan.Cancellation.IsCancellationRequested) { }
            token.ThrowIfCancellationRequested();
        }
    }

    public async ValueTask DisposeAsync()
    {
        (Task<WorkspaceFileIndex> Task, CancellationTokenSource Cancellation)[] pending;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            pending = scans.ToArray();
            foreach (var scan in pending)
            {
                scan.Cancellation.Cancel();
            }
            current = null;
            scans.Clear();
        }
        var failures = new List<Exception>();
        foreach (var scan in pending)
        {
            try
            {
                await scan.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (scan.Cancellation.IsCancellationRequested) { }
            catch (Exception error) { failures.Add(error); }
            finally
            {
                scan.Cancellation.Dispose();
            }
        }
        if (failures.Count > 0)
        {
            throw new AggregateException("文件发现失败；所有扫描资源已释放。", failures);
        }
    }
}
