namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Input;
using StudioX.Application.Editing;
using StudioX.Application.CodeIntelligence;
using StudioX.Foundation;

public partial class MainWindow
{
    private string? workspaceEditProject;
    private IReadOnlyList<WorkspaceFileChange> workspaceUndo = [];
    private WorkspaceBufferSnapshot[] CaptureWorkspaceBuffers() => editorDocuments.Where(e => !Path.IsPathRooted(e.Source.RelativePath))
        .Select(e => new WorkspaceBufferSnapshot(e.Source, e.Buffer.Text)).ToArray();

    private void InitializeWorkspaceEditing()
    {
        WorkspaceSearch.SearchRequested = replace => _ = RunAsync(token => SearchWorkspaceAsync(replace, token));
        WorkspaceSearch.ApplyRequested = () => _ = RunAsync(ApplyWorkspaceChangesAsync);
        WorkspaceSearch.UndoRequested = () => _ = RunAsync(_ => UndoWorkspaceChangesAsync());
        WorkspaceSearch.NavigateRequested = (change, offset) => _ = RunAsync(async token =>
        {
            if (projectDirectory != workspaceEditProject)
            {
                return;
            }
            await OpenSourceAsync(change.Path, token);
            if (activeEditor?.Buffer.Text != change.Before)
            {
                throw new StudioXException("SEARCH_STALE", "匹配结果已过期，请重新搜索。");
            }
            SourceEditor.CaretOffset = Math.Clamp(offset, 0, SourceEditor.Document.TextLength);
            SourceEditor.ScrollToLine(SourceEditor.TextArea.Caret.Line);
        });
        CommandBindings.Add(new(SourceCommands.WorkspaceFind, (_, _) => ShowWorkspaceSearch(false), (_, e) => e.CanExecute = projectDirectory is not null && !closing));
        CommandBindings.Add(new(SourceCommands.WorkspaceReplace, (_, _) => ShowWorkspaceSearch(true), (_, e) => e.CanExecute = projectDirectory is not null && !closing));
        CommandBindings.Add(new(SourceCommands.RenameSymbol, (_, _) => QueueRenameSymbol(), (_, e) => e.CanExecute = CanEditSource && !IsPythonDocument && NavigationReady && CanNavigateCode));
    }

    private void ShowWorkspaceSearch(bool replace)
    {
        if (projectDirectory is null)
        {
            return;
        }
        var selected = CanFindSource && SourceEditor.SelectionLength is > 0 and < 512 && !SourceEditor.SelectedText.Contains('\n') ? SourceEditor.SelectedText : null;
        ShowDocument(WorkspaceSearchTab);
        WorkspaceSearch.BeginSearch(selected, replace);
    }

    private async Task SearchWorkspaceAsync(bool replace, CancellationToken token)
    {
        var project = RequireProject();
        var buffers = CaptureWorkspaceBuffers();
        var options = WorkspaceSearch.Options;
        var result = await services.WorkspaceEdits.SearchAsync(project, options, replace ? WorkspaceSearch.Replacement.Text : null,
            WorkspaceSearch.Include.Text, WorkspaceSearch.Exclude.Text, WorkspaceSearch.IncludeDevice.IsChecked == true, buffers, token);
        if (project != projectDirectory)
        {
            return;
        }
        workspaceEditProject = project;
        WorkspaceSearch.SetResult(result.Files, $"{result.Files.Count} 个文件，{result.Files.Sum(f => f.Matches.Count)} 处匹配 · 已检查 {result.ScannedFiles} 个文件" +
            (result.Notices.Count > 0 ? $" · {result.Notices.Count} 条跳过说明见构建日志" : ""));
        foreach (var notice in result.Notices)
        {
            Log(notice);
        }
    }

    private async Task ApplyWorkspaceChangesAsync(CancellationToken token)
    {
        var project = RequireProject();
        if (project != workspaceEditProject || services.Debugger.IsActive)
        {
            throw new StudioXException("WORKSPACE_EDIT_STATE", "工程已切换或正在调试，无法应用修改。");
        }
        var changes = WorkspaceSearch.SelectedChanges;
        if (changes.Count == 0)
        {
            return;
        }
        var captured = CaptureWorkspaceBuffers();
        foreach (var change in changes)
        {
            await services.LocalHistory.CaptureAsync(project, change.Path, change.Before, "批量修改前", token);
        }
        await services.WorkspaceEdits.ValidateAsync(project, changes, captured, token);
        token.ThrowIfCancellationRequested();
        // 校验等待期间 UI 仍可接收输入；最后在同一 UI 调度段重新核对内存，随后整批应用。
        if (projectDirectory != project || captured.Any(b => FindEditor(b.Source.RelativePath)?.Buffer.Text != b.Text) ||
            changes.Any(c => FindEditor(c.Path) is { } opened && opened.Buffer.Text != c.Before))
        {
            throw new StudioXException("WORKSPACE_EDIT_STALE", "编辑内容在校验期间变化，请重新预览。");
        }
        var prepared = changes.Select(c => (Change: c, Editor: FindEditor(c.Path) ?? AddEditor(c.Source))).ToArray();
        foreach (var (change, editor) in prepared)
        {
            using (editor.Buffer.RunUpdate())
            {
                foreach (var edit in change.Matches.OrderByDescending(e => e.Start))
                {
                    editor.Buffer.Replace(edit.Start, edit.Length, edit.Replacement);
                }
            }
        }
        workspaceUndo = changes;
        WorkspaceSearch.Undo.IsEnabled = true;
        WorkspaceSearch.Apply.IsEnabled = false;
        WorkspaceSearch.Status.Text = $"已修改 {changes.Count} 个文件的编辑缓冲区；Ctrl+Shift+S 保存。可撤销本次批量修改。";
        QueueEditorCheckpoint();
    }

    private Task UndoWorkspaceChangesAsync()
    {
        if (projectDirectory != workspaceEditProject || services.Debugger.IsActive || workspaceUndo.Any(c => FindEditor(c.Path)?.Buffer.Text != c.After))
        {
            throw new StudioXException("WORKSPACE_UNDO_STALE", "部分文件在批量修改后已继续编辑或关闭；请在相应文件使用 Ctrl+Z，未批量覆盖后续修改。");
        }
        foreach (var change in workspaceUndo)
        {
            var editor = FindEditor(change.Path)!;
            using (editor.Buffer.RunUpdate())
            {
                editor.Buffer.Replace(0, editor.Buffer.TextLength, change.Before);
            }
        }
        workspaceUndo = [];
        WorkspaceSearch.Undo.IsEnabled = false;
        WorkspaceSearch.Status.Text = "已撤销本次批量修改；此前未保存的内容已保留。";
        return Task.CompletedTask;
    }

    private void QueueRenameSymbol(int? at = null)
    {
        if (!CanNavigateCode || !NavigationReady || IsPythonDocument || activeDocument!.IsReadOnly || services.Debugger.IsActive)
        {
            return;
        }
        var text = SourceEditor.Text;
        var offset = Math.Clamp(at ?? SourceEditor.CaretOffset, 0, text.Length);
        var start = offset;
        var end = offset;
        while (start > 0 && IsIdentifier(text[start - 1]))
        {
            start--;
        }
        while (end < text.Length && IsIdentifier(text[end]))
        {
            end++;
        }
        if (start == end)
        {
            return;
        }
        var oldName = text[start..end];
        var dialog = new SymbolRenameDialog(oldName) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.NewName == oldName)
        {
            return;
        }
        var project = RequireProject();
        var path = activeDocument.RelativePath;
        var buffers = CaptureWorkspaceBuffers();
        _ = RunAsync(async token =>
        {
            var changes = await services.Intelligence.RenameAsync(path, text, start, oldName, dialog.NewName, buffers, token);
            if (project != projectDirectory)
            {
                return;
            }
            workspaceEditProject = project;
            WorkspaceSearch.SetResult(changes, $"重命名 {oldName} → {dialog.NewName} · {changes.Count} 个文件 · 语义修改作为整体应用", rename: true);
            ShowDocument(WorkspaceSearchTab);
        });
    }

    private void ClearWorkspaceEditing()
    {
        workspaceEditProject = null;
        workspaceUndo = [];
        WorkspaceSearch.Clear();
        WorkspaceSearchTab.Visibility = Visibility.Collapsed;
    }
}
