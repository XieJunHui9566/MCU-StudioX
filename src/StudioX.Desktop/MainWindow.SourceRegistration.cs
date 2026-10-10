namespace StudioX.Desktop;

using System.Windows.Controls;
using StudioX.Application;
using StudioX.Application.BuildConfiguration;
using StudioX.Application.Editing;
using StudioX.Foundation;

public partial class MainWindow
{
    private readonly List<ProjectFileChange> sourceRegistrationMoves = [];
    private MenuItem? sourceRegistrationMenu;
    private SourceRegistrationPlan? sourceRegistrationUndo;
    private void InitializeSourceRegistration()
    {
        bool Enabled() => projectDirectory is not null && !closing && !services.Debugger.IsActive && pendingOperation.IsCompleted;
        Func<Task> show = () => RunAsync(token => ShowSourceRegistrationAsync(token));
        Func<Task> showTree = () => RunAsync(token => ShowSourceRegistrationAsync(token, explorerMenuEntry?.RelativePath ?? SelectedProjectEntry?.RelativePath));
        const string title = "源码登记与编译列表…";
        workbenchCommands.Add(new(title, "", Enabled, show));
        sourceRegistrationMenu = new MenuItem { Header = title, Command = new PluginUiCommand(show, Enabled) };
        ProjectToolsMenu.Items.Add(sourceRegistrationMenu);
        sourceContextMenu?.Items.Add(new MenuItem { Header = title, Command = new PluginUiCommand(show, Enabled) });
        explorerMenu?.Items.Add(new Separator());
        explorerMenu?.Items.Add(new MenuItem { Header = title, Command = new PluginUiCommand(showTree, Enabled) });
        const string undoTitle = "撤销上次源码登记";
        bool UndoEnabled() => Enabled() && sourceRegistrationUndo?.Context.ProjectDirectory == projectDirectory;
        Func<Task> undo = () => RunAsync(UndoSourceRegistrationAsync);
        workbenchCommands.Add(new(undoTitle, "", UndoEnabled, undo));
        ProjectToolsMenu.Items.Add(new MenuItem { Header = undoTitle, Command = new PluginUiCommand(undo, UndoEnabled) });
    }
    private void ObserveSourceRegistrationChanges(ProjectChangeBatch batch)
    {
        foreach (var move in batch.Changes.Where(change => change.Kind == ProjectFileChangeKind.Renamed && change.PreviousPath is not null))
        {
            if (!sourceRegistrationMoves.Contains(move))
            {
                sourceRegistrationMoves.Add(move);
            }
        }
        if (sourceRegistrationMoves.Count > 256)
        {
            sourceRegistrationMoves.RemoveRange(0, sourceRegistrationMoves.Count - 256);
        }
        if (batch.NamesChanged && sourceRegistrationMenu is not null)
        {
            sourceRegistrationMenu.Header = "源码登记与编译列表…（文件变化待核对）";
        }
    }
    private void ResetSourceRegistration()
    {
        sourceRegistrationMoves.Clear();
        sourceRegistrationUndo = null;
        if (sourceRegistrationMenu is not null)
        {
            sourceRegistrationMenu.Header = "源码登记与编译列表…";
        }
    }
    private async Task ShowSourceRegistrationAsync(CancellationToken token, string? selectedPath = null)
    {
        var project = RequireProject();
        EnsureNoActiveDebug();
        var selected = selectedPath ?? activeDocument?.RelativePath ?? SelectedProjectEntry?.RelativePath;
        var inventory = await services.SourceRegistration.DiscoverAsync(project, token);
        if (project != projectDirectory || closing)
        {
            return;
        }
        var configuration = inventory.Configurations.Contains("CMakeLists.txt") ? "CMakeLists.txt" : inventory.Configurations.FirstOrDefault();
        if (selected is not null)
        {
            if (inventory.Configurations.Contains(selected))
            {
                configuration = selected;
            }
            else
            {
                for (var directory = ProjectFileService.ParentDirectory(selected); directory.Length > 0; directory = ProjectFileService.ParentDirectory(directory))
                {
                    if (inventory.Configurations.Contains(directory + "/CMakeLists.txt"))
                    {
                        configuration = directory + "/CMakeLists.txt";
                        break;
                    }
                }
            }
        }
        if (currentProjectManifest?.Espressif?.Framework == "esp-idf" && configuration == "CMakeLists.txt")
        {
            configuration = inventory.Configurations.FirstOrDefault(path => path != "CMakeLists.txt");
        }
        if (configuration is null)
        {
            throw new StudioXException("SOURCE_CONFIGURATION", "未找到用户 CMakeLists.txt；请先核对工程构建入口。");
        }
        SourceRegistrationContext context;
        try
        {
            context = await services.SourceRegistration.ReadAsync(project, configuration, CaptureWorkspaceBuffers(), token);
        }
        catch (Exception error)
        {
            Log(error.ToString());
            await OpenSourceAsync(configuration, token);
            throw;
        }
        if (project != projectDirectory || closing)
        {
            return;
        }
        var window = new SourceRegistrationWindow(services.SourceRegistration, context, inventory.Configurations, inventory.Sources,
            sourceRegistrationMoves.ToArray(), CaptureWorkspaceBuffers, selected, Log)
        {
            Owner = this
        };
        if (window.ShowDialog() == true && window.Plan is { } plan)
        {
            await ApplySourceRegistrationAsync(plan, token);
        }
    }
    private async Task ApplySourceRegistrationAsync(SourceRegistrationPlan plan, CancellationToken token)
    {
        var project = plan.Context.ProjectDirectory;
        var change = plan.Change;
        if (project != projectDirectory || closing || services.Debugger.IsActive)
        {
            throw SourceRegistrationStale();
        }
        var editor = FindEditor(change.Path);
        var version = editor?.Buffer.Version;
        await services.LocalHistory.CaptureAsync(project, change.Path, change.Before, "源码登记前", token);
        await services.SourceRegistration.ValidateAsync(plan, CaptureWorkspaceBuffers(), token);
        token.ThrowIfCancellationRequested();
        if (project != projectDirectory || closing || services.Debugger.IsActive || FindEditor(change.Path) != editor ||
            (editor is not null && (editor.Buffer.Version != version || editor.Source.IsReadOnly || (editor == activeEditor && SourceEditor.IsReadOnly))))
        {
            throw SourceRegistrationStale();
        }
        editor ??= AddEditor(change.Source);
        ApplySourceRegistrationBuffer(change, editor);
        sourceRegistrationUndo = plan;
        ActivateEditor(editor);
        QueueEditorCheckpoint();
        if (sourceRegistrationMenu is not null)
        {
            sourceRegistrationMenu.Header = "源码登记与编译列表…";
        }
        Status.Text = "编译列表已进入未保存缓冲区 · Ctrl+Shift+S 保存后重新配置或编译；源码文件及 #include 保留，可撤销上次登记。";
    }
    private async Task UndoSourceRegistrationAsync(CancellationToken token)
    {
        var plan = sourceRegistrationUndo ?? throw SourceRegistrationStale();
        var project = plan.Context.ProjectDirectory;
        var previous = plan.Change;
        var editor = FindEditor(previous.Path);
        if (project != projectDirectory || services.Debugger.IsActive || editor is null || editor.Buffer.Text != previous.After || editor.Source.IsReadOnly)
        {
            throw SourceRegistrationStale();
        }
        var inverse = new WorkspaceFileChange(editor.Source, previous.After, previous.Before,
            previous.Matches.Select(edit => new TextSearchMatch(edit.Start, edit.Replacement.Length, previous.Before.Substring(edit.Start, edit.Length))).ToArray(), true);
        var version = editor.Buffer.Version;
        await services.WorkspaceEdits.ValidateAsync(project, [inverse], CaptureWorkspaceBuffers(), token);
        token.ThrowIfCancellationRequested();
        if (project != projectDirectory || services.Debugger.IsActive || FindEditor(previous.Path) != editor || editor.Buffer.Version != version ||
            editor.Source.IsReadOnly || (editor == activeEditor && SourceEditor.IsReadOnly))
        {
            throw SourceRegistrationStale();
        }
        ApplySourceRegistrationBuffer(inverse, editor);
        sourceRegistrationUndo = null;
        QueueEditorCheckpoint();
        Status.Text = "已撤销本次编译登记，原有未保存配置已保留；文件创建、删除和改名操作不撤销。";
    }
    private static void ApplySourceRegistrationBuffer(WorkspaceFileChange change, EditorDocumentSession editor)
    {
        try
        {
            using (editor.Buffer.RunUpdate())
            {
                foreach (var edit in change.Matches.OrderByDescending(edit => edit.Start))
                {
                    editor.Buffer.Replace(edit.Start, edit.Length, edit.Replacement);
                }
            }
        }
        catch (Exception error)
        {
            if (editor.Buffer.Text == change.After)
            {
                try
                {
                    using (editor.Buffer.RunUpdate())
                    {
                        foreach (var edit in change.Matches.OrderByDescending(edit => edit.Start))
                        {
                            editor.Buffer.Replace(edit.Start, edit.Replacement.Length, change.Before.Substring(edit.Start, edit.Length));
                        }
                    }
                }
                catch (Exception rollback) { throw new AggregateException("源码登记失败，缓冲区回退也失败；请核对保留的编辑内容。", error, rollback); }
            }
            throw new StudioXException("SOURCE_APPLY", "源码登记未完成；原始编辑事件异常已保留，请核对缓冲区。", error);
        }
    }
    private static StudioXException SourceRegistrationStale() => new("SOURCE_REGISTRATION_STALE", "工程、构建文件或编辑状态已变化，请重新预览；未覆盖后续修改。");
}
