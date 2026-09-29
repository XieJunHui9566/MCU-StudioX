namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Threading;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task ShowProductivityWorkspaceAsync(string fixture)
    {
        editorPersistenceEnabled = true;
        await LoadWorkbenchLayoutAsync();
        await RestoreLastEditorSessionAsync();
        if (projectDirectory is null)
        {
            await RunAsync(token => OpenProjectAsync(fixture, token));
        }
    }
    public async Task RenderProductivityPreviewAsync(string directory, string fixture, bool restore)
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
            await LoadWorkbenchLayoutAsync();
            await RestoreLastEditorSessionAsync();
            var expected = await JsonStore.ReadAsync<EditorWorkspaceSnapshot>(Path.Combine(directory, "expected-next-session.json"));
            var actual = CaptureEditorWorkspace();
            Check(actual.Documents.Select(d => (d.Path, d.Draft, d.Group, d.Selected, d.Caret, d.SelectionStart, d.SelectionLength)).SequenceEqual(expected.Documents.Select(d => (d.Path, d.Draft, d.Group, d.Selected, d.Caret, d.SelectionStart, d.SelectionLength))), "restart restores groups, selected tabs, order, drafts, caret and selection");
            Check(actual.ActivePath == expected.ActivePath && secondaryTabs?.Visibility == Visibility.Visible, "restart restores active group and visible split");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            UpdateLayout();
            Check(mirroredSession is not null && mirrorEditor.IsVisible && mirrorEditor.Document == mirroredSession.Buffer, "restart displays the inactive group's selected document rather than the welcome page");
            Render(this, Path.Combine(directory, "split-restored.png"));
            await File.WriteAllTextAsync(Path.Combine(directory, "recovery-next-result.txt"), "PASS " + checks.Count + "\n" + string.Join('\n', checks));
            return;
        }
        await OpenProjectAsync(fixture, CancellationToken.None);
        await OpenSourceAsync("src/main.c", CancellationToken.None);
        var first = activeEditor!;
        var original = first.Buffer.Text;
        var compact = "#include \"shared.h\"\nint shared_value=1;\nint main(void){if(shared_value){return helper();}return 0;}\n";
        SourceEditor.Text = compact;
        await PreviewFormatAsync(false);
        Check(WorkspaceSearch.SelectedChanges.Count == 1 && WorkspaceSearch.SelectedChanges[0].After != compact, "format command produces actual diff preview");
        await ApplyWorkspaceChangesAsync(CancellationToken.None);
        Check(first.Buffer.Text.Contains("if (shared_value)"), "format preview applies to unsaved document");
        first.Buffer.UndoStack.Undo();
        Check(first.Buffer.Text == compact, "format undone as one editor operation");
        var histories = await services.LocalHistory.ListAsync(fixture, first.Source.RelativePath);
        Check(histories.Any(h => h.Text == compact), "bulk format captures pre-edit local history");
        ShowDocument(first.Tab);
        SourceEditor.Text = original + "// draft retained across groups\n";
        SourceEditor.Select(0, 5);
        SourceEditor.CaretOffset = 5;
        CaptureEditorView();
        await OpenSourceAsync("src/other.c", CancellationToken.None);
        var second = activeEditor!;
        var secondText = second.Buffer.Text;
        MoveEditorGroup(second, 1);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Check(secondaryTabs?.IsVisible == true && mirroredSession == first && mirrorEditor.Document == first.Buffer, "split shows both documents with shared buffer ownership");
        SourceEditor.AppendText("// right draft\n");
        SourceEditor.Undo();
        Check(second.Buffer.Text == secondText, "right group edit and undo preserve buffer");
        ShowDocument(first.Tab);
        Check(SourceEditor.Document == first.Buffer && mirroredSession == second && SourceEditor.CaretOffset == 5, "activating left preserves caret and promotes full editor");
        SourceEditor.AppendText("// left edit\n");
        SourceEditor.Undo();
        Check(first.Buffer.Text == original + "// draft retained across groups\n", "left group retains its independent undo stack");
        MoveEditorGroup(first, 1, second);
        Check(secondaryTabs!.Items.IndexOf(first.Tab) < secondaryTabs.Items.IndexOf(second.Tab), "moving tabs supports explicit tab order");
        MoveEditorGroup(first, 0);
        ShowDocument(second.Tab);
        Width = 1100;
        Height = 760;
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Render(this, Path.Combine(directory, "split-compact.png"));
        Check(SourceEditor.ActualWidth > 250 && mirrorEditor.ActualWidth > 250, $"both editor columns remain usable in compact window: active={SourceEditor.ActualWidth}, mirror={mirrorEditor.ActualWidth}, left={editorGroups!.ColumnDefinitions[0].ActualWidth}, right={editorGroups.ColumnDefinitions[2].ActualWidth}");
        await ShowToolEnvironmentAsync();
        Check(environmentGrid!.Items.Count > 0, "tool environment lists installed versions");
        Render(this, Path.Combine(directory, "tool-environment.png"));
        ShowTroubleshooting("TOOLSET_MISSING: demonstration only");
        Check(troubleshootingTab?.Content is not null, "diagnostic guide renders actionable steps and original diagnostics");
        Render(this, Path.Combine(directory, "troubleshooting.png"));
        ShowDocument(first.Tab);
        ShowDocument(second.Tab);
        SourceEditor.Select(2, 3);
        SourceEditor.CaretOffset = 5;
        CaptureEditorView();
        await editorCheckpointTask;
        await PersistEditorCheckpointAsync();
        await services.WorkbenchLayout.SaveAsync(CaptureWorkbenchLayout());
        await JsonStore.WriteAsync(Path.Combine(directory, "expected-next-session.json"), CaptureEditorWorkspace());
        await File.WriteAllTextAsync(Path.Combine(directory, "next-ui-result.txt"), "PASS " + checks.Count + "\n" + string.Join('\n', checks));
    }
}
