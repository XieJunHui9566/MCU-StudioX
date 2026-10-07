namespace StudioX.Desktop;

using System.Windows.Threading;
using StudioX.Application.Editing;
using StudioX.Foundation;

public partial class MainWindow
{
    public async Task RenderStartupRecoveryPreviewAsync(string directory)
    {
        editorPersistenceEnabled = true;
        var expected = await JsonStore.ReadAsync<EditorWorkspaceSnapshot>(Path.Combine(directory, "expected-startup-session.json"));
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
        }
        await RestoreLastEditorSessionAsync();
        await OfferFirstProjectGuideAsync();
        Check(projectDirectory == expected.Project, "startup restores only the expected project or leaves no project open");
        var actual = CaptureEditorWorkspace();
        Check(actual.Documents.Select(d => (d.Path, d.Draft, d.Baseline, d.Caret, d.SelectionStart, d.SelectionLength))
            .SequenceEqual(expected.Documents.Select(d => (d.Path, d.Draft, d.Baseline, d.Caret, d.SelectionStart, d.SelectionLength))),
            "startup preserves document buffers and view state");
        if (File.Exists(Path.Combine(directory, "expected-failure.txt")))
        {
            Check(lastFailure.Length > 0 && ReferenceEquals(WorkspaceTabs.SelectedItem, troubleshootingTab),
                "malformed existing project retains the original diagnostic and troubleshooting page");
            await File.WriteAllTextAsync(Path.Combine(directory, "failure-diagnostic.txt"), lastFailure);
        }
        else
        {
            Check(lastFailure.Length == 0 && troubleshootingTab is null, "missing project never opens a troubleshooting page");
            if (expected.Project is null)
            {
                Check(ReferenceEquals(WorkspaceTabs.SelectedItem, WelcomeTab) && firstProjectTab is null,
                    "missing or explicitly closed project remains on the start page without reopening the first-project guide");
                Check(ProjectTree.Items.Count == 0 && editorDocuments.Count == 0 && activeEditor is null &&
                    !BuildButton.IsEnabled && !DownloadButton.IsEnabled && !CloseProjectMenu.IsEnabled && !services.Intelligence.IsReady,
                    "empty startup has no stale editor, file tree, language process or enabled project actions");
            }
        }
        var archiveExpectation = Path.Combine(directory, "expected-unavailable-session.json");
        if (File.Exists(archiveExpectation))
        {
            var original = await JsonStore.ReadAsync<EditorWorkspaceSnapshot>(archiveExpectation);
            var archives = Directory.GetFiles(Path.Combine(services.DataDirectory, "editor-sessions", "unavailable-projects"), "*.json");
            Check(archives.Length == 1, "deleted project recovery is archived once outside automatic recovery");
            var archived = await JsonStore.ReadAsync<EditorWorkspaceSnapshot>(archives.Single());
            Check(archived.Project == original.Project && archived.Documents.Select(d => (d.Path, d.Draft, d.Baseline))
                .SequenceEqual(original.Documents.Select(d => (d.Path, d.Draft, d.Baseline))), "archive preserves deleted-project drafts and save baselines");
        }
        await PersistEditorCheckpointAsync();
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        Render(this, Path.Combine(directory, "startup.png"));
        await File.WriteAllTextAsync(Path.Combine(directory, "startup-result.txt"), "PASS " + checks.Count + "\n" + string.Join('\n', checks));
    }
}
