namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application.Editing;

public partial class MainWindow
{
    private readonly DispatcherTimer editorCheckpointTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Task editorCheckpointTask = Task.CompletedTask;
    private EditorWorkspaceSnapshot? lastEditorCheckpoint;
    private bool editorPersistenceEnabled;
    private bool restoringEditorSession;
    private bool recoveryPending;
    private string? lastCheckpointError;

    private void InitializeEditorRecovery()
    {
        editorCheckpointTimer.Tick += (_, _) => QueueEditorCheckpoint();
        editorCheckpointTimer.Start();
    }

    private EditorWorkspaceSnapshot CaptureEditorWorkspace()
    {
        CaptureEditorView();
        return new(1, projectDirectory, activeEditor?.Source.RelativePath,
            editorDocuments.Where(e => !Path.IsPathRooted(e.Source.RelativePath)).Select(e => new StoredEditorDocument(
                e.Source.RelativePath, e.IsDirty ? e.Buffer.Text : null, e.IsDirty ? e.Source.Text : null, e.Source.DiskHash,
                e.Source.Encoding.CodePage, e.Source.Encoding.GetPreamble().Length > 0, e.Source.IsReadOnly,
                e.CaretOffset, e.SelectionStart, e.SelectionLength, e.VerticalOffset, e.HorizontalOffset, e.Group, DocumentTabs(e).SelectedItem == e.Tab)).ToArray(), DateTimeOffset.UtcNow);
    }

    private void QueueEditorCheckpoint()
    {
        if (!editorPersistenceEnabled || restoringEditorSession || closing || !editorCheckpointTask.IsCompleted || recoveryPending && projectDirectory is null)
        {
            return;
        }
        editorCheckpointTask = SaveEditorCheckpointWithReportAsync();
    }
    private async Task SaveEditorCheckpointWithReportAsync()
    {
        try
        {
            await PersistEditorCheckpointAsync();
            lastCheckpointError = null;
        }
        catch (Exception ex)
        {
            if (lastCheckpointError != ex.Message)
            {
                Log("编辑草稿备份失败：" + ex);
                lastCheckpointError = ex.Message;
            }
            Status.Text = "编辑草稿尚未备份：" + ex.Message;
        }
    }
    private async Task PersistEditorCheckpointAsync()
    {
        if (!editorPersistenceEnabled || restoringEditorSession || recoveryPending && projectDirectory is null)
        {
            return;
        }
        var snapshot = CaptureEditorWorkspace();
        if (lastEditorCheckpoint is { } previous && previous.Project == snapshot.Project && previous.ActivePath == snapshot.ActivePath && previous.Documents.SequenceEqual(snapshot.Documents))
        {
            return;
        }
        await services.EditorSessions.SaveAsync(snapshot);
        lastEditorCheckpoint = snapshot;
    }

    public Task RestoreLastEditorSessionAsync() => RunAsync(async token =>
    {
        if (!editorPersistenceEnabled || projectDirectory is not null)
        {
            return;
        }
        restoringEditorSession = true;
        try
        {
            var snapshot = await services.EditorSessions.ClaimLatestAsync(token);
            if (snapshot?.Project is null)
            {
                return;
            }
            recoveryPending = true;
            if (await SkipMissingEditorProjectAsync(snapshot.Project, token))
            {
                return;
            }
            IReadOnlyList<RecoveredEditorDocument> documents;
            try
            {
                documents = await services.EditorSessions.RestoreDocumentsAsync(snapshot, token);
                await OpenProjectAsync(snapshot.Project, token);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // 工程可能在检查后被删除；其它缺失文件仍保留原始错误，不当成正常空状态。
                if (projectDirectory is not null || !await SkipMissingEditorProjectAsync(snapshot.Project, token))
                {
                    throw;
                }
                return;
            }
            if (projectDirectory != snapshot.Project)
            {
                return;
            }
            ClearEditorDocuments();
            foreach (var document in documents)
            {
                var editor = AddEditor(document.Source);
                editor.Buffer.Text = document.Text;
                editor.Buffer.UndoStack.ClearAll();
                editor.CaretOffset = Math.Clamp(document.View.Caret, 0, editor.Buffer.TextLength);
                editor.SelectionStart = Math.Clamp(document.View.SelectionStart, 0, editor.Buffer.TextLength);
                editor.SelectionLength = Math.Clamp(document.View.SelectionLength, 0, editor.Buffer.TextLength - editor.SelectionStart);
                editor.VerticalOffset = double.IsFinite(document.View.Vertical) ? Math.Max(0, document.View.Vertical) : 0;
                editor.HorizontalOffset = double.IsFinite(document.View.Horizontal) ? Math.Max(0, document.View.Horizontal) : 0;
                if (document.View.Group == 1)
                {
                    MoveEditorGroup(editor, 1);
                }
                if (document.Notice is not null)
                {
                    Log(document.Notice);
                }
            }
            changingEditor = true;
            try
            {
                foreach (var group in editorDocuments.GroupBy(e => e.Group))
                {
                    var visible = group.FirstOrDefault(e => documents.Any(d => d.View.Path == e.Source.RelativePath && d.View.Selected)) ?? group.First();
                    DocumentTabs(visible).SelectedItem = visible.Tab;
                }
            }
            finally { changingEditor = false; }
            var selected = FindEditor(snapshot.ActivePath ?? "") ?? editorDocuments.FirstOrDefault();
            if (selected is not null)
            {
                ShowDocument(selected.Tab);
            }
            else
            {
                ShowDocument(WelcomeTab);
            }
            services.EditorSessions.CompleteRecovery();
            recoveryPending = false;
            Status.Text = $"已恢复 {documents.Count} 个标签，其中 {editorDocuments.Count(e => e.IsDirty)} 个保留未保存内容" +
                (documents.Any(d => d.Notice is not null) ? "；磁盘差异说明见构建日志。" : "。");
            QueueLiveDiagnostics();
        }
        finally { restoringEditorSession = false; }
    });

    private async Task<bool> SkipMissingEditorProjectAsync(string directory, CancellationToken token)
    {
        if (!await services.RecentProjects.RemoveIfMissingLocalAsync(directory, token))
        {
            return false;
        }
        var archive = await services.EditorSessions.ArchiveUnavailableRecoveryAsync(token);
        recoveryPending = false;
        await RefreshRecentAsync(token);
        ShowDocument(WelcomeTab);
        Status.Text = "就绪";
        Log("上次工程已删除，已回到开始页；编辑现场保留于：" + archive);
        return true;
    }
}
