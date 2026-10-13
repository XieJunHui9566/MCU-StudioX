using System.Collections.Concurrent;
using System.Threading.Channels;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Foundation;

if (args.Length is < 1 or > 2)
{
    throw new ArgumentException("New output directory and optional existing language runtime required.");
}
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root))
{
    throw new IOException("Use a new output directory.");
}
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
    checks.Add(message);
    Console.WriteLine("PASS " + message);
}
var project = Path.Combine(root, "中文 工程");
Directory.CreateDirectory(Path.Combine(project, "src"));
var batches = Channel.CreateUnbounded<ProjectChangeBatch>();
var observed = new ConcurrentQueue<ProjectChangeBatch>();
using (var session = new ProjectChangeService().Watch(project))
{
    session.Changed += batch => { observed.Enqueue(batch); batches.Writer.TryWrite(batch); };
    async Task<ProjectChangeBatch> Wait(Func<ProjectChangeBatch, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var batch = await batches.Reader.ReadAsync(timeout.Token);
            if (predicate(batch))
            {
                return batch;
            }
        }
    }
    await File.WriteAllTextAsync(Path.Combine(project, "src/测试.c"), "int value;\n");
    var created = await Wait(batch => batch.Changes.Any(change => change.Path == "src/测试.c"));
    Check(created.NamesChanged && created.AnalysisChanged, "native create observes Unicode paths and invalidates names and analysis");
    await File.AppendAllTextAsync(Path.Combine(project, "src/测试.c"), "int other;\n");
    var changed = await Wait(batch => batch.Changes.Any(change => change.Path == "src/测试.c"));
    Check(!changed.NamesChanged && changed.AnalysisChanged, "ordinary content write keeps the file-name index");
    File.Move(Path.Combine(project, "src/测试.c"), Path.Combine(project, "src/renamed.c"));
    var renamed = await Wait(batch => batch.Changes.Any(change => change.PreviousPath == "src/测试.c"));
    Check(renamed.Changes.Any(change => change.Path == "src/renamed.c" && change.Kind == ProjectFileChangeKind.Renamed), "native rename carries both paths");
    File.Delete(Path.Combine(project, "src/renamed.c"));
    Check((await Wait(batch => batch.Changes.Any(change => change.Path == "src/renamed.c"))).NamesChanged, "native delete refreshes names");
    Directory.CreateDirectory(Path.Combine(project, "src/original"));
    Directory.CreateDirectory(Path.Combine(project, "destination"));
    await File.WriteAllTextAsync(Path.Combine(project, "src/original/held.c"), "int held;\n");
    await Task.Delay(250);
    session.TrackPath("src/original/held.c");
    Directory.Move(Path.Combine(project, "src/original"), Path.Combine(project, "destination/moved"));
    var moved = await Wait(batch => batch.Changes.Any(change => change.PreviousPath == "src/original"));
    Check(moved.Changes.Any(change => change.Path == "destination/moved" && change.Kind == ProjectFileChangeKind.Renamed),
        "cross-parent native directory move retains identity despite delete and create notifications");
    await File.WriteAllTextAsync(Path.Combine(project, "src/old-owner.c"), "int identical;\n");
    await Wait(batch => batch.Changes.Any(change => change.Path == "src/old-owner.c"));
    session.TrackPath("src/old-owner.c");
    File.Delete(Path.Combine(project, "src/old-owner.c"));
    await File.WriteAllTextAsync(Path.Combine(project, "src/new-owner.c"), "int identical;\n");
    var different = await Wait(batch => batch.Changes.Any(change => change.Path == "src/new-owner.c"));
    Check(different.Changes.Any(change => change.Path == "src/new-owner.c" && change.Kind == ProjectFileChangeKind.Created),
        "equal contents in a newly created file do not impersonate the deleted file identity");
    for (var i = 0; i < 300; i++)
    {
        session.Notify(new("src/coalesced.c"));
    }
    Check((await Wait(batch => batch.Changes.Any(change => change.Path == "src/coalesced.c"))).Changes.Count(change => change.Path == "src/coalesced.c") == 1,
        "duplicate notifications coalesce into one path");
    session.Notify(new("src/ordered.c", ProjectFileChangeKind.Created));
    session.Notify(new("src/ordered.c"));
    Check((await Wait(batch => batch.Changes.Any(change => change.Path == "src/ordered.c"))).NamesChanged, "write after create does not lose structural invalidation");
    session.Notify(new("second", ProjectFileChangeKind.Renamed, "first"));
    session.Notify(new("third", ProjectFileChangeKind.Renamed, "second"));
    Check((await Wait(batch => batch.Changes.Any(change => change.Path == "third"))).Changes.Where(change => change.Kind == ProjectFileChangeKind.Renamed)
        .Select(change => change.PreviousPath).SequenceEqual(["first", "second"]), "directory rename chains preserve document identity order");
    session.Notify(new("src/file.c.tmp-test", ProjectFileChangeKind.Created));
    session.Notify(new(".build/firmware.elf", ProjectFileChangeKind.Created));
    session.Notify(new(".git/index"));
    session.Notify(new("src"));
    session.Notify(new(".build/compile_commands.json"));
    var config = await Wait(batch => batch.Changes.Any(change => change.Path == ".build/compile_commands.json"));
    Check(config.Changes.Count == 1 && config.AnalysisChanged && !config.NamesChanged, "build products and temporary writes are excluded while compile database is observed");
    session.Notify(new(".git/file.c", ProjectFileChangeKind.Renamed, "src/moved.c"));
    Check((await Wait(batch => batch.Changes.Any(change => change.Path == "src/moved.c"))).Changes.Any(change => change.Kind == ProjectFileChangeKind.Deleted),
        "rename into ignored directory is a workspace deletion");
    session.RequestRescan("original watcher overflow diagnostic");
    var overflow = await Wait(batch => batch.RequiresRescan);
    Check(overflow.Diagnostic == "original watcher overflow diagnostic", "watcher loss retains original diagnostic and requires complete reconciliation");
    for (var i = 0; i < 2100; i++)
    {
        session.Notify(new("src/burst" + i + ".c", ProjectFileChangeKind.Created));
    }
    Check((await Wait(batch => batch.RequiresRescan)).Diagnostic?.Contains("2,048", StringComparison.Ordinal) == true, "bounded queue turns a large burst into an observable rescan");
    var continuous = Task.Run(async () =>
    {
        for (var i = 0; i < 60; i++)
        {
            session.Notify(new("src/continuous.c"));
            await Task.Delay(20);
        }
    });
    await Wait(batch => batch.Changes.Any(change => change.Path == "src/continuous.c"));
    Check(!continuous.IsCompleted, "continuous writes cannot postpone synchronization indefinitely");
    await continuous;
    await Task.Delay(250);
    session.Dispose();
    var count = observed.Count;
    session.Notify(new("src/late.c"));
    await File.WriteAllTextAsync(Path.Combine(project, "src/late.c"), "int late;\n");
    await Task.Delay(250);
    Check(observed.Count == count, "disposed session publishes no late notifications");
}
var files = new ProjectFileService();
await using (var query = new WorkspaceFileQuerySession(new(files), project))
{
    Check((await query.SearchAsync("late")).SequenceEqual(["src/late.c"]), "query session discovers current disk");
    await File.WriteAllTextAsync(Path.Combine(project, "src/fresh.c"), "int fresh;\n");
    Check((await query.SearchAsync("fresh")).Count == 0, "typing reuses the current discovery snapshot");
    query.Invalidate();
    Check((await query.SearchAsync("fresh")).SequenceEqual(["src/fresh.c"]), "structural invalidation refreshes an already open quick picker");
    Check(!query.Invalidate(new(project, 1, [new("src/fresh.c", ProjectFileChangeKind.Created)])) &&
        !query.Invalidate(new(project, 2, [new(".build/compile_commands.json", ProjectFileChangeKind.Created)])),
        "atomic replacement of an indexed file and generated analysis inputs do not restart discovery");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try
    {
        await query.SearchAsync("", cancelled.Token);
        throw new InvalidOperationException("Cancelled query ran.");
    }
    catch (OperationCanceledException) { Check(true, "cancelled query does not cancel shared discovery"); }
    Check((await query.SearchAsync("fresh")).Count == 1, "shared discovery remains usable after query cancellation");
    File.Delete(Path.Combine(project, "src/fresh.c"));
    query.Invalidate();
    Check((await query.SearchAsync("fresh")).Count == 0, "deleted files disappear from the current picker session");
}
Check(!ProjectChangePolicy.Observes("build/nested/src.c") && ProjectChangePolicy.Observes("CMakeLists.txt") &&
    ProjectChangePolicy.IsAnalysisInput("device/manifest.json") && ProjectChangePolicy.IsAnalysisInput(".studiox/espressif-module.json"),
    "generated-directory policy is shared and explicit device and build metadata remain analysis inputs");
if (args.Length == 2)
{
    await LanguageFileChangeChecks.RunAsync(Path.GetFullPath(args[1]), Path.Combine(root, "language"), Check);
}
await JsonStore.WriteAsync(Path.Combine(root, "result.json"), new
{
    success = true,
    checks,
    nativeWatcherBatches = observed.Count,
    language = args.Length == 2 ? "passed" : "not_requested",
    hardware = false
});
