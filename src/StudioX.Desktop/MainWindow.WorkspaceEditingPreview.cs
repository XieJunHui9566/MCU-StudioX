namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderWorkspaceEditingPreviewAsync(string directory, string fixture, bool restore)
    {
        editorPersistenceEnabled = true;
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        if (restore)
        {
            var expected = await JsonStore.ReadAsync<EditorWorkspaceSnapshot>(Path.Combine(directory, "expected-editor-session.json"));
            await RestoreLastEditorSessionAsync();
            var actual = CaptureEditorWorkspace();
            Check(actual.Project == expected.Project && actual.ActivePath == expected.ActivePath, "restart restores project and selected tab");
            Check(actual.Documents.Count == expected.Documents.Count && actual.Documents.Select(d => d.Path).SequenceEqual(expected.Documents.Select(d => d.Path)), "restart restores tab order");
            Check(actual.Documents.Zip(expected.Documents).All(pair => pair.First.Draft == pair.Second.Draft && pair.First.Baseline == pair.Second.Baseline), "restart restores unsaved buffers and disk baselines");
            Check(actual.Documents.Zip(expected.Documents).All(pair => pair.First.Caret == pair.Second.Caret && pair.First.SelectionStart == pair.Second.SelectionStart && pair.First.SelectionLength == pair.Second.SelectionLength), "restart restores caret and selection");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "session-restored.png"));
            await File.WriteAllTextAsync(Path.Combine(directory, "recovery-result.txt"), "PASS " + checks.Count + "\n" + string.Join('\n', checks));
            return;
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        Check(projectDirectory == fixture && activeEditor is not null, "generic ARM project opens without hardware");
        var source = activeEditor!.Source;
        var dirty = source.Text + "// shared_value draft\n";
        SourceEditor.Text = dirty;
        ShowWorkspaceSearch(true);
        WorkspaceSearch.Pattern.Text = "shared_value";
        WorkspaceSearch.Replacement.Text = "counter";
        WorkspaceSearch.WholeWord.IsChecked = true;
        WorkspaceSearch.MatchCase.IsChecked = true;
        WorkspaceSearch.Include.Text = "src/*.c;include/*.h";
        await SearchWorkspaceAsync(true, CancellationToken.None);
        Check(WorkspaceSearch.SelectedChanges.Count == 3, "project replacement preview includes three files");
        await ApplyWorkspaceChangesAsync(CancellationToken.None);
        Check(editorDocuments.Count == 3 && editorDocuments.All(e => e.IsDirty), "batch changes open closed files as unsaved editor tabs");
        Check((await services.Files.ReadAsync(fixture, "src/main.c")).Text == source.Text, "batch application does not write disk");
        FindEditor("src/main.c")!.Buffer.UndoStack.Undo();
        Check(FindEditor("src/main.c")!.Buffer.Text == dirty, "one Ctrl+Z restores a file including its prior draft");
        try
        {
            await UndoWorkspaceChangesAsync();
            throw new InvalidOperationException("Expected stale batch undo rejection");
        }
        catch (StudioXException ex) when (ex.Code == "WORKSPACE_UNDO_STALE") { Check(true, "batch undo rejects independently changed buffers"); }
        FindEditor("src/main.c")!.Buffer.UndoStack.Redo();
        await UndoWorkspaceChangesAsync();
        Check(FindEditor("src/main.c")!.Buffer.Text == dirty && !FindEditor("src/other.c")!.IsDirty, "batch undo restores all earlier buffer states");
        await SearchWorkspaceAsync(true, CancellationToken.None);
        foreach (var theme in new[] { ThemeService.Dark, ThemeService.Light })
        {
            ApplyTheme(theme);
            UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Render(this, Path.Combine(directory, "workspace-search-" + theme.Id + ".png"));
        }
        ApplyTheme(ThemeService.Dark);
        ShowDocument(FindEditor("src/main.c")!.Tab);
        SourceEditor.CaretOffset = dirty.IndexOf("shared_value", StringComparison.Ordinal);
        QueuePythonReferences();
        await navigationTask;
        Check(PythonReferences.Items.Count >= 4 && WorkspaceTabs.SelectedItem == PythonReferencesTab, "C/C++ references route to shared results page");
        ShowDocument(FindEditor("src/main.c")!.Tab);
        var rename = await services.Intelligence.RenameAsync("src/main.c", dirty, dirty.IndexOf("shared_value", StringComparison.Ordinal),
            "shared_value", "shared_counter", CaptureWorkspaceBuffers());
        workspaceEditProject = fixture;
        WorkspaceSearch.SetResult(rename, "语义重命名 shared_value → shared_counter", true);
        WorkspaceSearch.Files.SelectedItem = WorkspaceSearch.Files.Items.Cast<WorkspaceChangeRow>().Single(r => r.Path == "src/main.c");
        WorkspaceSearch.ResultTabs.SelectedIndex = 1;
        ShowDocument(WorkspaceSearchTab);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        Render(this, Path.Combine(directory, "semantic-rename.png"));
        await ApplyWorkspaceChangesAsync(CancellationToken.None);
        Check(FindEditor("src/other.c")!.Buffer.Text.Contains("int shared_value = 7; return shared_value;", StringComparison.Ordinal), "UI semantic rename preserves local shadow");
        await UndoWorkspaceChangesAsync();
        ShowDocument(FindEditor("src/main.c")!.Tab);
        SourceEditor.Text = dirty.Replace("return shared_value + helper()", "return missing_value + helper()", StringComparison.Ordinal);
        QueueLiveDiagnostics();
        for (var attempt = 0; attempt < 60 && !problemRows.Any(r => r.Message.Contains("missing_value", StringComparison.Ordinal)); attempt++)
        {
            await Task.Delay(150);
        }
        Check(problemRows.Any(r => r.Origin.StartsWith("实时", StringComparison.Ordinal) && r.Message.Contains("missing_value", StringComparison.Ordinal)), "actual editor shows live clangd error in problems panel");
        Check(diagnosticRenderer!.Markers.Any(m => m.Diagnostic.Message.Contains("missing_value", StringComparison.Ordinal)), "live error underlined in source editor");
        var dependency = FindEditor("include/shared.h")!;
        dependency.Buffer.Text = dependency.Buffer.Text.Replace("helper", "renamed_helper", StringComparison.Ordinal);
        Check(problemRows.Length == 0 && diagnosticRenderer.Markers.Count == 0, "editing inactive header revokes displayed diagnostics immediately before debounce");
        Check(ProblemsTab.Header.ToString()!.Contains("正在分析", StringComparison.Ordinal), "zero visible problems during refresh is explicitly labeled as analysis pending");
        SourceEditor.Text = dirty;
        for (var attempt = 0; attempt < 60 && !problemRows.Any(r => r.Message.Contains("helper", StringComparison.Ordinal)); attempt++)
        {
            await Task.Delay(150);
        }
        Check(problemRows.Any(r => r.Origin.StartsWith("实时", StringComparison.Ordinal) && r.Message.Contains("helper", StringComparison.Ordinal)), "dependent editor shows actual error from unsaved header");
        await CloseWorkspaceTabAsync(dependency.Tab, _ => System.Windows.MessageBoxResult.No);
        Check(FindEditor("include/shared.h") is null && problemRows.Length == 0, "closing inactive dirty header immediately clears its dependent errors");
        for (var attempt = 0; attempt < 60 && !services.Intelligence.GetDiagnostics().Any(b => b.Path == "src/main.c" && b.Text == dirty && b.IsComplete); attempt++)
        {
            await Task.Delay(150);
        }
        RefreshDiagnosticMarkers();
        Check(services.Intelligence.GetDiagnostics().Any(b => b.Path == "src/main.c" && b.Text == dirty && b.IsComplete && b.Items.All(i => i.Severity != 1)) && problemRows.All(r => r.Origin != "实时 · clangd" || !r.Message.Contains("helper", StringComparison.Ordinal)), "discarding header draft reparses unchanged source and restores clean live result");
        SourceEditor.Text = dirty.Replace("return shared_value + helper()", "return missing_value + helper()", StringComparison.Ordinal);
        for (var attempt = 0; attempt < 60 && !problemRows.Any(r => r.Message.Contains("missing_value", StringComparison.Ordinal)); attempt++)
        {
            await Task.Delay(150);
        }
        Check(problemRows.Any(r => r.Message.Contains("missing_value", StringComparison.Ordinal)), "subsequent editor change still publishes a new real error");
        ShowBottom(4);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        UpdateLayout();
        Check(ProblemsGrid.ActualWidth > 600 && ProblemsGrid.Columns[^1].ActualWidth > 200, "problems panel columns remain readable after layout");
        Render(this, Path.Combine(directory, "live-diagnostics.png"));
        SourceEditor.Select(0, 4);
        SourceEditor.CaretOffset = 4;
        CaptureEditorView();
        await editorCheckpointTask;
        await PersistEditorCheckpointAsync();
        await JsonStore.WriteAsync(Path.Combine(directory, "expected-editor-session.json"), CaptureEditorWorkspace());
        await File.WriteAllTextAsync(Path.Combine(directory, "result.txt"), "PASS " + checks.Count + "; real WPF + clangd; no hardware\n" + string.Join('\n', checks));
    }
}
