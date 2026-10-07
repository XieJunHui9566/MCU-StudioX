namespace StudioX.Application.Editing;

using System.Text;
using StudioX.Foundation;

/// <summary>独立用户数据中的编辑现场；每个实例持有租约，恢复不会读取仍运行实例的草稿。</summary>
public sealed class EditorSessionStore : IDisposable
{
    private readonly string root;
    private readonly string directory;
    private readonly FileStream lease;
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? pendingRecoveryLease;
    private string? pendingRecoveryState;
    public bool HasPreviousSession
    {
        get; private set;
    }
    public EditorSessionStore(string dataDirectory)
    {
        root = Path.Combine(dataDirectory, "editor-sessions");
        directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        lease = new FileStream(Path.Combine(directory, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task SaveAsync(EditorWorkspaceSnapshot snapshot, CancellationToken token = default)
    {
        Validate(snapshot);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // 恢复尚未成功时，临时空窗口不能被误记成用户主动关闭工程。
            if (pendingRecoveryLease is not null && snapshot.Project is null)
            {
                return;
            }
            await JsonStore.WriteAsync(Path.Combine(directory, "state.json"), snapshot, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<EditorWorkspaceSnapshot?> ClaimLatestAsync(CancellationToken token = default)
    {
        if (pendingRecoveryLease is not null)
        {
            throw new StudioXException("SESSION_PENDING", "上一个编辑现场尚未完成恢复。");
        }
        foreach (var candidate in Directory.EnumerateDirectories(root).Where(p => p != directory)
                     .Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                     .OrderByDescending(p => File.GetLastWriteTimeUtc(Path.Combine(p, "state.json"))))
        {
            var state = Path.Combine(candidate, "state.json");
            if (!File.Exists(state))
            {
                continue;
            }
            FileStream owner;
            try
            {
                owner = new(Path.Combine(candidate, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { continue; }
            var retained = false;
            try
            {
                if (!File.Exists(state))
                {
                    continue;
                }
                if (new FileInfo(state).Length > 128L * 1024 * 1024)
                {
                    throw new StudioXException("SESSION_SIZE", "编辑现场文件过大，原恢复记录已保留。");
                }
                var snapshot = await JsonStore.ReadAsync<EditorWorkspaceSnapshot>(state, token).ConfigureAwait(false);
                Validate(snapshot);
                HasPreviousSession = true;
                if (snapshot.Project is null)
                {
                    // 最近一次明确关闭工程的记录是恢复边界，不能重新打开更早的工程。
                    return null;
                }
                // 窗口完成恢复之前保留旧记录与租约；工程打开失败不能吞掉原草稿。
                await SaveAsync(snapshot with
                {
                    UpdatedUtc = DateTimeOffset.UtcNow
                }, token).ConfigureAwait(false);
                pendingRecoveryState = state;
                pendingRecoveryLease = owner;
                retained = true;
                return snapshot;
            }
            finally
            {
                if (!retained)
                {
                    owner.Dispose();
                }
            }
        }
        return null;
    }

    public void CompleteRecovery()
    {
        if (pendingRecoveryState is null)
        {
            return;
        }
        File.Delete(pendingRecoveryState);
        pendingRecoveryState = null;
        pendingRecoveryLease?.Dispose();
        pendingRecoveryLease = null;
    }

    public async Task<string> ArchiveUnavailableRecoveryAsync(CancellationToken token = default)
    {
        var state = pendingRecoveryState
            ?? throw new StudioXException("SESSION_PENDING", "没有待保留的编辑现场。");
        var snapshot = await JsonStore.ReadAsync<EditorWorkspaceSnapshot>(state, token).ConfigureAwait(false);
        Validate(snapshot);
        var archive = Path.Combine(root, "unavailable-projects", Guid.NewGuid().ToString("N") + ".json");
        // 先保留完整草稿，再写入无工程现场；归档不参与自动恢复，后续启动也不回退到旧工程。
        await JsonStore.WriteAsync(archive, snapshot, token).ConfigureAwait(false);
        CompleteRecovery();
        await SaveAsync(new(1, null, null, [], DateTimeOffset.UtcNow), token).ConfigureAwait(false);
        return archive;
    }

    public async Task<IReadOnlyList<RecoveredEditorDocument>> RestoreDocumentsAsync(EditorWorkspaceSnapshot snapshot, CancellationToken token = default)
    {
        Validate(snapshot);
        if (snapshot.Project is null)
        {
            return [];
        }
        var result = new List<RecoveredEditorDocument>();
        var files = new ProjectFileService();
        foreach (var document in snapshot.Documents)
        {
            var path = PathBoundary.Resolve(snapshot.Project, document.Path);
            var exists = File.Exists(path);
            SourceDocument? disk = null;
            string? readError = null;
            try
            {
                if (exists)
                {
                    disk = await files.ReadAsync(snapshot.Project, document.Path, token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (document.Draft is not null && ex is IOException or UnauthorizedAccessException or StudioXException)
            {
                readError = ex.ToString();
            }
            if (document.Draft is null)
            {
                if (disk is not null)
                {
                    result.Add(new(disk, disk.Text, document, null));
                }
                continue;
            }
            if (disk?.Text == document.Draft)
            {
                result.Add(new(disk, disk.Text, document, null));
                continue;
            }
            Encoding encoding = document.CodePage switch
            {
                65001 => new UTF8Encoding(document.HasBom, true),
                1200 => new UnicodeEncoding(false, document.HasBom, true),
                1201 => new UnicodeEncoding(true, document.HasBom, true),
                _ => throw new StudioXException("SESSION_ENCODING", "恢复记录包含不支持的文本编码。")
            };
            var source = new SourceDocument(document.Path, document.Baseline!, encoding, document.DiskHash,
                document.ReadOnly || disk?.IsReadOnly == true, disk?.ReadOnlyReason, IsMissing: !exists);
            var notice = readError is not null ? document.Path + "：磁盘文件无法读取，已恢复草稿及原保存基线。\n" + readError
                : disk is null ? document.Path + "：原文件已删除，草稿保留；保存将重新创建文件。"
                : disk.DiskHash != document.DiskHash ? document.Path + "：磁盘已变化，草稿及原保存基线已保留；保存前需处理冲突。" : null;
            result.Add(new(source, document.Draft, document, notice));
        }
        return result;
    }

    private static void Validate(EditorWorkspaceSnapshot snapshot)
    {
        if (snapshot.FormatVersion != 1 || snapshot.Documents.Count > 1000 ||
            snapshot.Documents.Sum(d => (long)(d.Draft?.Length ?? 0) + (d.Baseline?.Length ?? 0)) > 32L * 1024 * 1024)
        {
            throw new StudioXException("SESSION_LIMIT", "编辑现场超出支持范围（1000 个标签、32 Mi 字符草稿与基线），请先保存或关闭部分文件。");
        }
        if (snapshot.Project is null && snapshot.Documents.Count != 0)
        {
            throw new StudioXException("SESSION_PROJECT", "编辑现场缺少工程目录。");
        }
        if (snapshot.Project is not null)
        {
            if (!Path.IsPathFullyQualified(snapshot.Project))
            {
                throw new StudioXException("SESSION_PROJECT", "恢复工程目录必须为绝对路径。");
            }
            foreach (var document in snapshot.Documents)
            {
                _ = PathBoundary.Resolve(snapshot.Project, document.Path);
                if (document.Draft is not null && document.Baseline is null)
                {
                    throw new StudioXException("SESSION_BASELINE", "草稿缺少保存基线，原记录已保留。");
                }
            }
        }
    }
    public void Dispose()
    {
        pendingRecoveryLease?.Dispose();
        lease.Dispose();
        gate.Dispose();
    }
}
