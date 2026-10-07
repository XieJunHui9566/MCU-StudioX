using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

internal static class EditorRecoveryChecks
{
    public static async Task RunAsync(string output, string validProject, Action<string> pass)
    {
        var root = Path.Combine(output, "editor-recovery");
        Directory.CreateDirectory(root);
        var empty = new EditorWorkspaceSnapshot(1, null, null, [], DateTimeOffset.UtcNow);
        var baseline = "int main(void) { for (;;) {} }\n";
        var document = new StoredEditorDocument("src/main.c", baseline + "// retained draft\n", baseline, "baseline-hash",
            65001, false, false, 4, 1, 2, 0, 0);
        var snapshot = new EditorWorkspaceSnapshot(1, validProject, document.Path, [document], DateTimeOffset.UtcNow);
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            pass("PASS: " + message);
        }
        async Task Seed(string data, EditorWorkspaceSnapshot state)
        {
            using var store = new EditorSessionStore(data);
            await store.SaveAsync(state);
        }

        var closedData = Path.Combine(root, "closed-data");
        await Seed(closedData, snapshot);
        var previousState = Directory.GetFiles(Path.Combine(closedData, "editor-sessions"), "state.json", SearchOption.AllDirectories).Single();
        File.SetLastWriteTimeUtc(previousState, DateTime.UtcNow.AddMinutes(-10));
        await Seed(closedData, empty);
        using (var store = new EditorSessionStore(closedData))
        {
            Check(await store.ClaimLatestAsync() is null && store.HasPreviousSession, "explicitly closed project stops recovery before older sessions");
            Check((await JsonStore.ReadAsync<EditorWorkspaceSnapshot>(previousState)).Documents.Single().Draft == document.Draft,
                "older unclaimed drafts remain intact behind an empty-session boundary");
        }
        var archivedData = Path.Combine(root, "archived-data");
        await Seed(archivedData, snapshot);
        string archive;
        using (var store = new EditorSessionStore(archivedData))
        {
            Check((await store.ClaimLatestAsync())?.Project == validProject, "existing project remains eligible for normal session recovery");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await store.ArchiveUnavailableRecoveryAsync(cancelled.Token);
                throw new InvalidOperationException("Cancelled archive accepted.");
            }
            catch (OperationCanceledException) { }
            Check(Directory.GetFiles(Path.Combine(archivedData, "editor-sessions"), "state.json", SearchOption.AllDirectories)
                .Any(path => File.ReadAllText(path).Contains("retained draft", StringComparison.Ordinal)),
                "cancelled archive cannot discard the pending original draft");
            archive = await store.ArchiveUnavailableRecoveryAsync();
            var retained = await JsonStore.ReadAsync<EditorWorkspaceSnapshot>(archive);
            Check(retained.Project == snapshot.Project && retained.Documents.Single().Draft == document.Draft &&
                retained.Documents.Single().Baseline == baseline, "unavailable-project archive preserves project, draft and save baseline");
        }
        for (var restart = 0; restart < 2; restart++)
        {
            using var store = new EditorSessionStore(archivedData);
            Check(await store.ClaimLatestAsync() is null && File.Exists(archive), "restart " + (restart + 1) + " does not reopen archived project or erase its draft");
        }

        // 为真实 WPF 独立进程准备夹具；所有目录均为本次新建验证输出，不访问用户工程。
        string CopyFixture(string name)
        {
            var destination = Path.Combine(root, "fixtures", name);
            foreach (var file in Directory.EnumerateFiles(validProject, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(validProject, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            return destination;
        }
        var olderProject = CopyFixture("older-valid");
        foreach (var name in new[] { "deleted-directory", "deleted-manifest", "explicitly-closed", "existing-project", "malformed-project" })
        {
            var ui = Path.Combine(root, "ui-cases", name);
            var data = Path.Combine(ui, "user-data");
            var project = CopyFixture(name);
            await Seed(data, snapshot with
            {
                Project = olderProject
            });
            var olderState = Directory.GetFiles(Path.Combine(data, "editor-sessions"), "state.json", SearchOption.AllDirectories).Single();
            File.SetLastWriteTimeUtc(olderState, DateTime.UtcNow.AddMinutes(-10));
            var latest = name == "explicitly-closed" ? empty : snapshot with
            {
                Project = project
            };
            await Seed(data, latest);
            var history = new RecentProjectService(data);
            if (name == "existing-project")
            {
                await history.RememberAsync(name, project);
            }
            if (name == "deleted-directory")
            {
                Directory.Delete(project, recursive: true);
            }
            if (name == "deleted-manifest")
            {
                File.Delete(Path.Combine(project, ".studiox", "project.json"));
            }
            if (name == "malformed-project")
            {
                await File.WriteAllTextAsync(Path.Combine(project, ".studiox", "project.json"), "{");
                await File.WriteAllTextAsync(Path.Combine(ui, "expected-failure.txt"), "Existing malformed manifest must retain diagnostics.");
            }
            if (name.StartsWith("deleted-", StringComparison.Ordinal))
            {
                Check(await history.RemoveIfMissingLocalAsync(project), name + " is recognized even without a recent-project entry");
                await JsonStore.WriteAsync(Path.Combine(ui, "expected-unavailable-session.json"), latest);
            }
            await JsonStore.WriteAsync(Path.Combine(ui, "expected-startup-session.json"), name == "existing-project" ? latest : empty);
        }
        pass("PASS: isolated WPF startup fixtures cover deleted directory, missing manifest, explicit close, normal draft restore and malformed manifest");
    }
}
