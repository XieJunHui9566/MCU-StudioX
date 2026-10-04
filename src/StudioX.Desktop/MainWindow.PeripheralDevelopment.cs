namespace StudioX.Desktop;

using System.Windows.Controls;
using StudioX.Application.Editing;
using StudioX.Application.PeripheralDevelopment;
using StudioX.Foundation;

public partial class MainWindow
{
    private PeripheralProjectAddition? peripheralUndo;
    private void InitializePeripheralDevelopment()
    {
        const string title = "外设开发辅助…";
        bool Enabled() => projectDirectory is not null;
        workbenchCommands.Add(new(title, "", Enabled, ShowPeripheralDevelopmentAsync));
        ProjectToolsMenu.Items.Add(new MenuItem { Header = title, Command = new PluginUiCommand(ShowPeripheralDevelopmentAsync, Enabled) });
        sourceContextMenu?.Items.Add(new MenuItem { Header = title, Command = new PluginUiCommand(ShowPeripheralDevelopmentAsync, Enabled) });
        const string undoTitle = "撤销上次外设添加";
        bool UndoEnabled() => peripheralUndo?.Context.ProjectDirectory == projectDirectory && peripheralUndo is not null && !closing && !services.Debugger.IsActive;
        Func<Task> undo = () => RunAsync(UndoPeripheralAdditionAsync);
        workbenchCommands.Add(new(undoTitle, "", UndoEnabled, undo));
        ProjectToolsMenu.Items.Add(new MenuItem { Header = undoTitle, Command = new PluginUiCommand(undo, UndoEnabled) });
        sourceContextMenu?.Items.Add(new MenuItem { Header = undoTitle, Command = new PluginUiCommand(undo, UndoEnabled) });
    }

    private async Task ShowPeripheralDevelopmentAsync()
    {
        if (projectDirectory is not { } project)
        {
            return;
        }
        var target = CaptureTemplateTarget(start: 0, length: 0);
        // 首批代码以 C 生成和验收；其他文档仍可查看和复制预览。
        var insertable = target is not null && Path.GetExtension(target.Session.Source.RelativePath).Equals(".c", StringComparison.OrdinalIgnoreCase);
        CloseCodeAssistance();
        try
        {
            Status.Text = "正在核对工程锁定的外设 SDK…";
            var context = await services.PeripheralDevelopment.ReadAsync(project);
            if (closing || project != projectDirectory)
            {
                return;
            }
            PeripheralComponentContext? component = null;
            string? issue = null;
            if (insertable && target is not null)
            {
                try
                {
                    component = await services.PeripheralDevelopment.ReadComponentAsync(context, target.Session.Source.RelativePath, CaptureWorkspaceBuffers());
                }
                catch (Exception ex) { issue = ex.Message; Log(ex.ToString()); }
            }
            if (closing || project != projectDirectory)
            {
                return;
            }
            var window = new PeripheralDevelopmentWindow(services.PeripheralDevelopment, context, insertable, Log, component, issue) { Owner = this };
            if (window.ShowDialog() == true && window.AdditionResult is { } addition && target is not null)
            {
                await ApplyPeripheralAdditionAsync(target, addition, CancellationToken.None);
            }
            else
            {
                Status.Text = "外设辅助已关闭。";
            }
        }
        catch (Exception ex) { Status.Text = "外设开发辅助：" + ex.Message; Log(ex.ToString()); }
    }

    private async Task ApplyPeripheralAdditionAsync(TemplateInsertionTarget target, PeripheralProjectAddition addition, CancellationToken token)
    {
        var project = addition.Context.ProjectDirectory;
        if (projectDirectory != project || !TemplateTargetCurrent(target) || services.Debugger.IsActive)
        {
            throw Stale();
        }
        var states = addition.Files.Select(file => (File: file, Editor: FindEditor(file.Path), Version: FindEditor(file.Path)?.Buffer.Version)).ToArray();
        var buffers = CaptureWorkspaceBuffers();
        foreach (var change in addition.Changes)
        {
            await services.LocalHistory.CaptureAsync(project, change.Path, change.Before, "外设添加前", token);
        }
        await services.PeripheralDevelopment.ValidateAdditionAsync(addition, buffers, token);
        token.ThrowIfCancellationRequested();
        if (!TemplateTargetCurrent(target) || projectDirectory != project || services.Debugger.IsActive || states.Any(state =>
            FindEditor(state.File.Path) != state.Editor || (state.Editor is not null && (state.Editor.Buffer.Version != state.Version ||
                state.Editor.Buffer.Text != state.File.Before || state.Editor.Source.IsReadOnly))))
        {
            throw Stale();
        }
        // 所有异步校验结束后，在同一 UI 调度段修改两个缓冲区；不写盘，也不覆盖未保存配置。
        var prepared = addition.Changes.Select(file => (File: file, Editor: FindEditor(file.Path) ?? AddEditor(file.Source))).ToArray();
        ApplyPeripheralBufferChanges(prepared);
        peripheralUndo = addition;
        SourceEditor.CaretOffset = Math.Max(0, addition.Files[0].After.Length - addition.Files[0].Before.Length);
        SourceEditor.Select(SourceEditor.CaretOffset, 0);
        SourceEditor.Focus();
        QueueEditorCheckpoint();
        Status.Text = "外设代码和组件依赖已添加 · Ctrl+Shift+S 保存全部；右键可撤销上次外设添加。请在任务中调用初始化函数。";
    }

    private async Task UndoPeripheralAdditionAsync(CancellationToken token)
    {
        var addition = peripheralUndo ?? throw Stale();
        var project = addition.Context.ProjectDirectory;
        if (project != projectDirectory || services.Debugger.IsActive || addition.Changes.Any(file =>
            FindEditor(file.Path) is not { } editor || editor.Buffer.Text != file.After || editor.Source.IsReadOnly ||
            (editor == activeEditor && SourceEditor.IsReadOnly)))
        {
            throw UndoStale();
        }
        var inverse = addition.Changes.Select(file =>
        {
            var editor = FindEditor(file.Path)!;
            return new WorkspaceFileChange(editor.Source, file.After, file.Before,
                file.Matches.Select(edit => new StudioX.Application.TextSearchMatch(edit.Start, edit.Replacement.Length, file.Before.Substring(edit.Start, edit.Length))).ToArray(), true);
        }).ToArray();
        var states = inverse.Select(file => (File: file, Editor: FindEditor(file.Path)!, Version: FindEditor(file.Path)!.Buffer.Version)).ToArray();
        await services.WorkspaceEdits.ValidateAsync(project, inverse, CaptureWorkspaceBuffers(), token);
        token.ThrowIfCancellationRequested();
        if (project != projectDirectory || services.Debugger.IsActive || states.Any(state => FindEditor(state.File.Path) != state.Editor ||
            state.Editor.Buffer.Version != state.Version || state.Editor.Source.IsReadOnly || (state.Editor == activeEditor && SourceEditor.IsReadOnly)))
        {
            throw UndoStale();
        }
        ApplyPeripheralBufferChanges(states.Select(state => (state.File, state.Editor)).ToArray());
        peripheralUndo = null;
        QueueEditorCheckpoint();
        Status.Text = "已一起撤销外设代码和本次补齐的依赖；添加前未保存的内容已保留。";
    }

    private static void ApplyPeripheralBufferChanges(IReadOnlyList<(WorkspaceFileChange File, EditorDocumentSession Editor)> changes)
    {
        try
        {
            foreach (var (file, editor) in changes)
            {
                using (editor.Buffer.RunUpdate())
                {
                    foreach (var edit in file.Matches.OrderByDescending(edit => edit.Start))
                    {
                        editor.Buffer.Replace(edit.Start, edit.Length, edit.Replacement);
                    }
                }
            }
        }
        catch (Exception applyError)
        {
            var errors = new List<Exception> { applyError };
            // 编辑事件也可能失败；只回滚仍符合本次结果的缓冲区，不覆盖事件引入的其他修改。
            foreach (var (file, editor) in changes.Reverse())
            {
                try
                {
                    if (editor.Buffer.Text == file.Before)
                    {
                        continue;
                    }
                    if (editor.Buffer.Text != file.After)
                    {
                        throw new StudioXException("PERIPHERAL_ROLLBACK", file.Path + " 在编辑事件中发生其他修改，请核对缓冲区；未强制覆盖。");
                    }
                    using (editor.Buffer.RunUpdate())
                    {
                        foreach (var edit in file.Matches.OrderByDescending(edit => edit.Start))
                        {
                            editor.Buffer.Replace(edit.Start, edit.Replacement.Length, file.Before.Substring(edit.Start, edit.Length));
                        }
                    }
                }
                catch (Exception rollbackError) { errors.Add(rollbackError); }
            }
            throw new AggregateException("外设修改未完成，请核对源码与组件配置；原始应用/回滚诊断已保留。", errors);
        }
    }

    private static StudioXException Stale() => new("PERIPHERAL_ADDITION_STALE", "工程、文件或编辑状态已变化，请重新打开外设辅助；未添加代码或依赖。");
    private static StudioXException UndoStale() => new("PERIPHERAL_UNDO_STALE", "外设添加后相关文件已继续编辑、关闭或变为只读，无法整体撤销。请在对应文件使用 Ctrl+Z，保留后续修改。");
}
