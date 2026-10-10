namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application.Editing;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

public partial class MainWindow
{
    /// <summary>真实 WPF 与原生文件通知验收；夹具、草稿和删除恢复都在独立目录。</summary>
    public async Task RenderProjectSynchronizationPreviewAsync(string directory)
    {
        var checks = new List<string>();
        var observedBatches = new System.Collections.Concurrent.ConcurrentQueue<ProjectChangeBatch>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            File.WriteAllLines(Path.Combine(directory, "progress.txt"), checks);
        }
        async Task Wait(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                while (!condition())
                {
                    await Task.Delay(25, timeout.Token);
                }
                await projectSyncTask.WaitAsync(timeout.Token);
            }
            catch
            {
                await JsonStore.WriteAsync(Path.Combine(directory, "failed-sync-state.json"), new
                {
                    batches = observedBatches.ToArray(),
                    documents = editorDocuments.Select(editor => new { editor.Source.RelativePath, editor.IsDirty, editor.DiskConflict }).ToArray(),
                    log = BuildLog.Text,
                    loading = loadingProjectDirectories.Select(pair => new { path = (pair.Key.Tag as StudioX.Application.ProjectEntry)?.RelativePath, status = pair.Value.Status.ToString() }).ToArray()
                });
                throw;
            }
        }
        async Task<string> Fixture(string name)
        {
            var root = Path.Combine(directory, name);
            Directory.CreateDirectory(Path.Combine(root, ".studiox"));
            Directory.CreateDirectory(Path.Combine(root, "device"));
            Directory.CreateDirectory(Path.Combine(root, "src/nested"));
            Directory.CreateDirectory(Path.Combine(root, "device/sdk/include"));
            await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"), "# offline fixture\n");
            await File.WriteAllTextAsync(Path.Combine(root, "device/sdk/include/system.h"), "#pragma once\n");
            await File.WriteAllTextAsync(Path.Combine(root, "src/main.c"), "int main(void) { return 0; }\n");
            await File.WriteAllTextAsync(Path.Combine(root, "src/nested/helper.c"), "int helper(void) { return 1; }\n");
            await JsonStore.WriteAsync(Path.Combine(root, ".studiox/project.json"), new ProjectManifest(1, "file-sync-fixture", "offline.pack", "1.0.0", "offline",
                "offline-device", "plain", "offline.tools", "1.0.0", "gcc"));
            var device = new DeviceDefinition("offline-device", "Offline", "arm", 0x08000000, 65536, 0x20000000, 16384,
                "offline.tools", "1.0.0", "gcc", [], [], [], [], "linker.ld", [], [], [new ProjectTemplate("plain", "Plain", "Offline", "src/main.c")]);
            await JsonStore.WriteAsync(Path.Combine(root, "device/manifest.json"), new PackManifest(1, "offline.pack", "1.0.0", "Offline", "Offline", [device]));
            return root;
        }
        var project = await Fixture("中文 文件同步");
        await OpenProjectAsync(project, CancellationToken.None);
        var originalSession = projectChangeSession!;
        originalSession.Changed += observedBatches.Enqueue;
        var main = activeEditor!;
        var rootNode = ProjectTree.Items[0];
        var sourceNode = await FindProjectNodeAsync("src");
        var mainNode = await FindProjectNodeAsync("src/main.c");
        mainNode!.IsSelected = true;
        SourceEditor.CaretOffset = 4;
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), "int main(void) { return 2; }\n");
        await Wait(() => main.Source.Text.Contains("return 2", StringComparison.Ordinal));
        Check(!main.IsDirty && SourceEditor.Text == main.Source.Text && SourceEditor.CaretOffset == 4, "clean external modification updates text while preserving caret and saved state");
        Check(ReferenceEquals(ProjectTree.Items[0], rootNode) && ReferenceEquals(await FindProjectNodeAsync("src/main.c"), mainNode), "ordinary content update preserves tree node identity");
        main.Buffer.Insert(main.Buffer.TextLength, "// IDE saved\n");
        await SaveEditorAsync(project, main, CancellationToken.None);
        buildDiagnostics["src/main.c"] = (main.Source.Text, [new("src/main.c", 1, 1, false, "fresh build error")], "构建");
        await Task.Delay(350);
        await projectSyncTask;
        await JsonStore.WriteAsync(Path.Combine(directory, "save-notifications.json"), observedBatches.ToArray());
        Check(buildDiagnostics.ContainsKey("src/main.c"), "late notification from IDE save retains diagnostics for that saved build input");
        main.Buffer.Insert(main.Buffer.TextLength, "// unsaved\n");
        var unsaved = main.Buffer.Text;
        var baseline = main.Source.DiskHash;
        var canUndo = main.Buffer.UndoStack.CanUndo;
        buildDiagnostics["src/main.c"] = (main.Source.Text, [new("src/main.c", 1, 1, false, "old error")], "构建");
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), "int main(void) { return 3; }\n");
        await Wait(() => main.DiskConflict is not null);
        Check(main.Buffer.Text == unsaved && main.Source.DiskHash == baseline && main.Buffer.UndoStack.CanUndo == canUndo,
            "dirty external modification preserves text, disk baseline and undo");
        Check(main.Label.Text.Contains("磁盘冲突", StringComparison.Ordinal) && buildDiagnostics.Count == 0, "conflict is visible and obsolete build diagnostics are withdrawn");
        try
        {
            await SaveEditorAsync(project, main, CancellationToken.None);
            throw new InvalidOperationException("External disk content overwritten.");
        }
        catch (StudioXException error) when (error.Code == "EDITOR_FILE_CHANGED") { Check(true, "saving a conflicting buffer refuses disk overwrite"); }
        await File.WriteAllTextAsync(Path.Combine(project, "src/新增.c"), "int added;\n");
        await Wait(() => sourceNode!.Items.OfType<System.Windows.Controls.TreeViewItem>().Any(node => node.Tag is StudioX.Application.ProjectEntry entry && entry.RelativePath == "src/新增.c"));
        Check(ReferenceEquals(sourceNode, await FindProjectNodeAsync("src")) && ReferenceEquals(mainNode, await FindProjectNodeAsync("src/main.c")) && SelectedProjectEntry?.RelativePath == "src/main.c",
            "file creation reconciles only its parent and retains expansion and selection");
        await OpenSourceAsync("src/nested/helper.c", CancellationToken.None);
        var helper = activeEditor!;
        helper.Buffer.Insert(helper.Buffer.TextLength, "// unsaved helper\n");
        var helperText = helper.Buffer.Text;
        var nestedNode = await FindProjectNodeAsync("src/nested");
        nestedNode!.IsExpanded = true;
        await LoadChildrenAsync(nestedNode);
        Directory.Move(Path.Combine(project, "src/nested"), Path.Combine(project, "src/改名目录"));
        await Wait(() => helper.Source.RelativePath == "src/改名目录/helper.c");
        Check(helper.Buffer.Text == helperText && EditorBreadcrumb.Text.Contains("改名目录", StringComparison.Ordinal) &&
            ReferenceEquals(nestedNode, await FindProjectNodeAsync("src/改名目录")), "external directory rename remaps dirty tab, breadcrumb and existing tree subtree");
        Directory.CreateDirectory(Path.Combine(project, "src/moved"));
        await Wait(() => sourceNode!.Items.OfType<System.Windows.Controls.TreeViewItem>().Any(node => node.Tag is StudioX.Application.ProjectEntry entry && entry.RelativePath == "src/moved"));
        Directory.Move(Path.Combine(project, "src/改名目录"), Path.Combine(project, "src/moved/改名目录"));
        await Wait(() => helper.Source.RelativePath == "src/moved/改名目录/helper.c");
        Check(ReferenceEquals(nestedNode, await FindProjectNodeAsync("src/moved/改名目录")) && nestedNode!.IsExpanded && helper.Buffer.Text == helperText,
            "moving a directory into an unloaded parent preserves loaded subtree and dirty contents");
        await OpenSourceAsync("src/新增.c", CancellationToken.None);
        var added = activeEditor!;
        var addedText = added.Buffer.Text;
        File.Delete(Path.Combine(project, "src/新增.c"));
        await Wait(() => added.Source.IsMissing);
        Check(added.IsDirty && added.Buffer.Text == addedText && added.Label.Text.Contains("已删除", StringComparison.Ordinal), "deleted clean file remains recoverable and requires close confirmation");
        Check(CaptureEditorWorkspace().Documents.Single(document => document.Path == added.Source.RelativePath).Draft == addedText,
            "deleted clean text is included in the recovery draft");
        await SaveEditorAsync(project, added, CancellationToken.None);
        Check(await File.ReadAllTextAsync(Path.Combine(project, "src/新增.c")) == addedText && !added.Source.IsMissing && !added.IsDirty,
            "saving retained deleted text recreates exactly that file");
        await Task.Delay(250);
        File.Delete(Path.Combine(project, "src/新增.c"));
        await Wait(() => added.Source.IsMissing);
        await File.WriteAllTextAsync(Path.Combine(project, "src/新增.c"), "int other_owner;\n");
        // 不等回调刷新基线，保存也必须由文件服务拒绝覆盖重现的文件。
        try
        {
            await SaveEditorAsync(project, added, CancellationToken.None);
            throw new InvalidOperationException("Reappeared file overwritten.");
        }
        catch (IOException) { Check(true, "deleted-file recovery cannot overwrite a file that reappears"); }
        await Wait(() => !added.Source.IsMissing && added.DiskConflict is not null);
        Check(added.Buffer.Text == addedText && await File.ReadAllTextAsync(Path.Combine(project, "src/新增.c")) == "int other_owner;\n",
            "different reappeared file preserves both the recovery draft and its new disk contents");
        await File.WriteAllBytesAsync(Path.Combine(project, "src/新增.c"), [0, 1, 2, 0]);
        await Wait(() => BuildLog.Text.Contains("二进制文件", StringComparison.Ordinal));
        Check(added.Buffer.Text == addedText && added.IsDirty && !added.Source.IsMissing,
            "binary replacement preserves readable text and a protected save baseline");
        var commands = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingOperation = commands.Task;
        await File.WriteAllTextAsync(Path.Combine(project, "src/moved/改名目录/helper.c"), "int helper(void) { return 7; }\n");
        await Task.Delay(350);
        Check(helper.Source.DiskHash != (await services.Files.ReadAsync(project, helper.Source.RelativePath)).DiskHash && helper.DiskConflict is null,
            "observed updates wait for an active project command");
        commands.SetResult();
        await Wait(() => helper.DiskConflict is not null);
        Check(helper.Buffer.Text == helperText, "queued command update retains unsaved contents after completion");
        await using (var query = new WorkspaceFileQuerySession(services.WorkspaceDiscovery, project))
        {
            Check((await query.SearchAsync("新增")).Count == 1, "current file discovery sees the live tree state");
            var picker = new QuickPickWindow("自动同步快开验收", async (text, token) => (await query.SearchAsync(text, token)).Select(path => new QuickPickItem(path, path, path)).ToArray());
            void Changed(ProjectChangeBatch batch)
            {
                if (query.Invalidate(batch))
                {
                    _ = picker.RefreshResultsAsync();
                }
            }
            ProjectFilesSynchronized += Changed;
            try
            {
                await picker.RefreshResultsAsync();
                await File.WriteAllTextAsync(Path.Combine(project, "src/picker_new.c"), "int picker_new;\n");
                await Wait(() => picker.Results.Items.OfType<QuickPickItem>().Any(item => (string)item.Value == "src/picker_new.c"));
                Check(true, "existing quick picker updates without closing or typing another query");
            }
            finally
            {
                ProjectFilesSynchronized -= Changed;
                picker.Close();
            }
        }
        originalSession.RequestRescan("original simulated loss diagnostic");
        await Wait(() => BuildLog.Text.Contains("original simulated loss diagnostic", StringComparison.Ordinal));
        Check(helper.Buffer.Text == helperText && main.Buffer.Text == unsaved, "observable loss recovery preserves all dirty buffers");
        Render(this, Path.Combine(directory, "file-conflicts.png"));
        var next = await Fixture("next-workspace");
        originalSession.Notify(new("src/main.c", ProjectFileChangeKind.Renamed, "src/late.c"));
        await OpenProjectAsync(next, CancellationToken.None, _ => MessageBoxResult.No);
        await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), "old workspace late write\n");
        await Task.Delay(350);
        await projectSyncTask;
        Check(projectDirectory == next && activeEditor?.Buffer.Text == "int main(void) { return 0; }\n" &&
            editorDocuments.Count == 1 && !ReferenceEquals(projectChangeSession, originalSession), "project transition retires watcher and rejects late old-workspace changes");
        await CloseProjectAsync(CancellationToken.None, _ => MessageBoxResult.No);
        Check(projectChangeSession is null, "closing project releases the change session");
        await JsonStore.WriteAsync(Path.Combine(directory, "result.json"), new
        {
            success = true,
            checks,
            hardware = false
        });
    }
}
