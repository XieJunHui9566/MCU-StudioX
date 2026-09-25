namespace StudioX.Desktop;

using StudioX.Application;

public partial class MainWindow
{
    private Task aiEditorSyncTask = Task.CompletedTask;

    /// <summary>工具写入成功就更新编辑器；后续模型请求失败或取消也不撤回已落盘的改动。</summary>
    private void OnAiToolCompletedForEditor(AiAgentProgress progress)
    {
        if (progress.ToolName is not ("project_edit_file" or "project_patch_file" or
            "project_create_file" or "project_create_directory" or "external_project_copy") ||
            string.IsNullOrWhiteSpace(progress.Text) || projectDirectory is not { } project)
            return;

        var generation = aiProjectGeneration;
        var path = progress.Text;
        var created = progress.ToolName == "project_create_file";
        var directoryCreated = progress.ToolName == "project_create_directory";
        var copied = progress.ToolName == "external_project_copy";
        aiEditorSyncTask = SyncAiEditorAfterPreviousAsync(aiEditorSyncTask, project, generation,
            path, created, directoryCreated, copied);
    }

    private async Task SyncAiEditorAfterPreviousAsync(Task previous, string project, int generation,
        string path, bool created, bool directoryCreated, bool copied)
    {
        // 文件写入按模型工具调用顺序完成；UI 同步也保持这个顺序。
        await previous;
        if (!IsCurrentAiEditorProject(project, generation)) return;
        try
        {
            if (copied || directoryCreated)
            {
                // 目录与外部库可能包含二进制资源；刷新工程树和语言索引即可。
                RefreshProjectTree(path);
                Status.Text = copied ? $"AI 已复制 {path} 到工程，工程树已更新。"
                    : $"AI 已创建目录 {path}，工程树已更新。";
                try { await RefreshExplorerLanguageAsync(CancellationToken.None); }
                catch (Exception ex) { Log("AI 复制后语言服务刷新失败：" + ex); }
            }
            else await SyncAiEditorAfterWriteAsync(project, generation, path, created);
        }
        catch (Exception ex)
        {
            Log("AI 写入后同步编辑器失败：" + path + "：" + ex);
            if (IsCurrentAiEditorProject(project, generation))
                AppendAiTranscript("IDE", $"{path} 已写入磁盘，但编辑器刷新失败：{ex.Message}。请检查磁盘文件。");
        }
    }

    private bool IsCurrentAiEditorProject(string project, int generation) =>
        !closing && !closed && generation == aiProjectGeneration &&
        string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase);

    private async Task SyncAiEditorAfterWriteAsync(string project, int generation,
        string path, bool created)
    {
        var disk = await services.Files.ReadAsync(project, path);
        if (!IsCurrentAiEditorProject(project, generation)) return;

        if (FindEditor(path) is { } session)
        {
            if (!string.Equals(disk.DiskHash, session.Source.DiskHash, StringComparison.Ordinal))
            {
                if (session.IsDirty)
                {
                    // 绝不覆盖用户在工具授权后的新编辑；旧磁盘哈希会让保存操作拒绝覆盖。
                    AppendAiTranscript("IDE", $"{path} 已由 AI 写入磁盘，但编辑器有未保存修改。当前缓冲区已保留；请核对两份内容后再保存。");
                    Status.Text = $"{path}：磁盘文件已变化，编辑器中的未保存修改已保留。";
                    return;
                }

                var active = ReferenceEquals(activeEditor, session);
                if (active) CaptureEditorView();
                if (session.Changed is not null) session.Buffer.TextChanged -= session.Changed;
                try
                {
                    session.Source = disk;
                    session.Buffer.Text = disk.Text;
                    session.Buffer.UndoStack.ClearAll();
                    UpdateEditorHeader(session);
                }
                finally { if (session.Changed is not null) session.Buffer.TextChanged += session.Changed; }

                if (active)
                {
                    var start = Math.Clamp(session.SelectionStart, 0, session.Buffer.TextLength);
                    SourceEditor.Select(start, Math.Clamp(session.SelectionLength, 0, session.Buffer.TextLength - start));
                    SourceEditor.CaretOffset = Math.Clamp(session.CaretOffset, 0, session.Buffer.TextLength);
                    RefreshActiveEditorMetadata();
                    UpdateEditorPosition();
                    RefreshDiagnosticMarkers();
                    RefreshDebugMarkers();
                    QueueOutlineRefresh(clear: true);
                }
            }
        }
        else if (created)
        {
            // 新文件直接显示在源码标签，用户无需到资源树中再打开一次。
            ShowSource(disk);
        }

        RefreshProjectTree(created ? path : null);
        Status.Text = $"AI 已写入 {path}，编辑器和工程树已更新。";
        try { await RefreshExplorerLanguageAsync(CancellationToken.None); }
        catch (Exception ex) { Log("AI 写入后语言服务刷新失败：" + ex); }
    }
}
