namespace StudioX.Desktop;

using StudioX.Application;

public partial class MainWindow
{
    private Task aiEditorSyncTask = Task.CompletedTask;
    private EditorDocumentSynchronizer? editorSynchronizer;

    private EditorDocumentSynchronizer EditorSynchronizer => editorSynchronizer ??= new(
        session => ReferenceEquals(activeEditor, session), CaptureEditorView,
        UpdateEditorHeader, RestoreEditorViewAfterDiskSync);

    private void RestoreEditorViewAfterDiskSync(EditorDocumentSession session)
    {
        var start = Math.Clamp(session.SelectionStart, 0, session.Buffer.TextLength);
        SourceEditor.Select(start, Math.Clamp(session.SelectionLength, 0, session.Buffer.TextLength - start));
        SourceEditor.CaretOffset = Math.Clamp(session.CaretOffset, 0, session.Buffer.TextLength);
        SourceEditor.ScrollToVerticalOffset(session.VerticalOffset);
        SourceEditor.ScrollToHorizontalOffset(session.HorizontalOffset);
        RefreshActiveEditorMetadata();
        UpdateEditorPosition();
        RefreshDiagnosticMarkers();
        RefreshDebugMarkers();
        QueueOutlineRefresh(clear: true);
    }

    /// <summary>工具写入成功就更新编辑器；后续模型请求失败或取消也不撤回已落盘的改动。</summary>
    private void OnAiToolCompletedForEditor(AiAgentProgress progress)
    {
        if (progress.ToolName is not ("project_edit_file" or "project_patch_file" or
            "project_create_file" or "project_create_directory" or "external_project_copy" or "ag32_pin_mapping_enable" or "ag32_pin_plan_apply") ||
            string.IsNullOrWhiteSpace(progress.Text) || projectDirectory is not { } project)
        {
            return;
        }

        var generation = aiProjectGeneration;
        var path = progress.Text;
        var created = progress.ToolName is "project_create_file" or "ag32_pin_mapping_enable";
        var directoryCreated = progress.ToolName == "project_create_directory";
        var copied = progress.ToolName == "external_project_copy";
        aiEditorSyncTask = SyncAiEditorAfterPreviousAsync(aiEditorSyncTask, project, generation,
            path, created, directoryCreated, copied);
        if (progress.ToolName is "ag32_pin_mapping_enable" or "ag32_pin_plan_apply")
        {
            aiEditorSyncTask = RefreshAg32AfterAiEnableAsync(aiEditorSyncTask, project, generation);
        }
    }

    private async Task RefreshAg32AfterAiEnableAsync(Task previous, string project, int generation)
    {
        await previous;
        if (!IsCurrentAiEditorProject(project, generation))
        {
            return;
        }
        try
        {
            await RefreshAg32PinMappingStatusAsync(CancellationToken.None);
        }
        catch (Exception error)
        {
            Log("AI 启用 AG32 映射后刷新状态失败：" + error);
        }
    }

    private async Task SyncAiEditorAfterPreviousAsync(Task previous, string project, int generation,
        string path, bool created, bool directoryCreated, bool copied)
    {
        // 文件写入按模型工具调用顺序完成；UI 同步也保持这个顺序。
        await previous;
        if (!IsCurrentAiEditorProject(project, generation))
        {
            return;
        }
        try
        {
            if (copied || directoryCreated)
            {
                // 目录与外部库可能包含二进制资源；刷新工程树和语言索引即可。
                await SynchronizeProjectFilesAsync(new(project, 0,
                    [new(path, StudioX.Application.Editing.ProjectFileChangeKind.Created)]));
                Status.Text = copied ? $"AI 已复制 {path} 到工程，工程文件已同步。"
                    : $"AI 已创建目录 {path}，工程文件已同步。";
            }
            else
            {
                await SyncAiEditorAfterWriteAsync(project, generation, path, created);
            }
        }
        catch (Exception ex)
        {
            Log("AI 写入后同步编辑器失败：" + path + "：" + ex);
            if (IsCurrentAiEditorProject(project, generation))
            {
                AppendAiTranscript("IDE", $"{path} 已写入磁盘，但编辑器刷新失败：{ex.Message}。请检查磁盘文件。");
            }
        }
    }

    private bool IsCurrentAiEditorProject(string project, int generation) =>
        !closing && !closed && generation == aiProjectGeneration &&
        string.Equals(projectDirectory, project, StringComparison.OrdinalIgnoreCase);

    private async Task SyncAiEditorAfterWriteAsync(string project, int generation,
        string path, bool created)
    {
        if (!IsCurrentAiEditorProject(project, generation))
        {
            return;
        }
        var review = await SynchronizeProjectFilesAsync(new(project, 0,
            [new(path, created ? StudioX.Application.Editing.ProjectFileChangeKind.Created : StudioX.Application.Editing.ProjectFileChangeKind.Changed)]));
        if (!IsCurrentAiEditorProject(project, generation))
        {
            return;
        }
        if (created && FindEditor(path) is null)
        {
            var disk = await services.Files.ReadAsync(project, path);
            if (IsCurrentAiEditorProject(project, generation))
            {
                ShowSource(disk);
            }
        }
        if (review)
        {
            AppendAiTranscript("IDE", $"{path} 已写入磁盘，冲突的编辑内容已保留，请核对带标记的标签。");
        }
        else
        {
            Status.Text = $"AI 已写入 {path}，工程文件已同步。";
        }
    }
    private async Task<bool> RefreshAiWorkspaceAfterToolsAsync(string project, AiAgentTurn turn, int generation)
    {
        var changedWorkspace = turn.ProtocolMessages?.Any(message =>
            message.ToolCalls?.Any(AiWorkspaceChangePolicy.MayChangeWorkspace) == true) == true;
        if (!changedWorkspace)
        {
            return false;
        }
        if (!IsCurrentAiEditorProject(project, generation))
        {
            return false;
        }
        return await ResynchronizeProjectFilesAsync();
    }
}
