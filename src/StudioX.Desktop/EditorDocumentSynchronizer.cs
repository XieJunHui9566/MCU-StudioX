namespace StudioX.Desktop;

using StudioX.Application;

/// <summary>统一应用磁盘变化；脏缓冲区保留原保存基线，让文件服务阻止覆盖外部改动。</summary>
internal sealed class EditorDocumentSynchronizer(
    Func<EditorDocumentSession, bool> isActive,
    Action captureActiveView,
    Action<EditorDocumentSession> refreshHeader,
    Action<EditorDocumentSession> restoreActiveView)
{
    public EditorDiskSyncResult Apply(EditorDocumentSession session, SourceDocument disk)
    {
        if (string.Equals(disk.DiskHash, session.Source.DiskHash, StringComparison.Ordinal))
        {
            session.Source = disk;
            session.DiskConflict = null;
            refreshHeader(session);
            return EditorDiskSyncResult.Unchanged;
        }

        if (session.IsDirty)
        {
            session.Source = session.Source with
            {
                IsMissing = false
            };
            session.DiskConflict = "磁盘文件已变化，未保存内容已保留；请核对磁盘与编辑内容后再保存。";
            refreshHeader(session);
            return EditorDiskSyncResult.UnsavedChangesPreserved;
        }

        var active = isActive(session);
        if (active)
        {
            captureActiveView();
        }

        if (session.Changed is not null)
        {
            session.Buffer.TextChanged -= session.Changed;
        }

        try
        {
            session.Source = disk;
            session.DiskConflict = null;
            session.Buffer.Text = disk.Text;
            // 磁盘变化使旧撤销基线失效，继续撤销可能把 AI 已写入的代码重新写成旧内容。
            session.Buffer.UndoStack.ClearAll();
            refreshHeader(session);
        }
        finally
        {
            if (session.Changed is not null)
            {
                session.Buffer.TextChanged += session.Changed;
            }
        }

        if (active)
        {
            restoreActiveView(session);
        }

        return EditorDiskSyncResult.Updated;
    }

    public void PreserveUnavailable(EditorDocumentSession session, bool missing, string diagnostic)
    {
        session.Source = session.Source with
        {
            IsMissing = missing
        };
        session.DiskConflict = diagnostic;
        refreshHeader(session);
    }
}
