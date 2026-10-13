using StudioX.Application;
using StudioX.Foundation;

internal static class RecentProjectActionsChecks
{
    internal static async Task RunAsync(string output, Action<string> pass)
    {
        var root = Path.Combine(output, "recent-actions");
        var data = Path.Combine(root, "data");
        var runtime = Path.Combine(root, "runtime");
        var recent = new RecentProjectService(data, runtime);
        async Task<string> Fixture(string name)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(directory, ".studiox"));
            await File.WriteAllTextAsync(Path.Combine(directory, ".studiox/project.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(directory, "source.c"), "// fixture only\n");
            await recent.RememberAsync("same name", directory);
            return directory;
        }
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            pass("PASS: recent actions " + message);
        }
        async Task Reject(string directory, string? openProject, string code)
        {
            try
            {
                await recent.DeleteAsync(directory, openProject);
                throw new InvalidOperationException("Deletion accepted: " + code);
            }
            catch (StudioXException ex) when (ex.Code == code) { pass("PASS: recent actions rejected " + code); }
        }
        var kept = await Fixture("keep");
        var removed = await Fixture("remove-record");
        await recent.RemoveAsync(removed);
        Check(File.Exists(Path.Combine(removed, "source.c")) && (await recent.LoadAsync()).Single().Directory == kept,
            "record removal keeps disk contents and targets path rather than duplicate name");
        await Reject(kept, kept, "PROJECT_DELETE_OPEN");
        await Reject(root, kept, "PROJECT_DELETE_OPEN");
        await Reject(Path.GetPathRoot(root)!, null, "PROJECT_DELETE_PROTECTED");
        await Reject(data, null, "PROJECT_DELETE_PROTECTED");
        await Reject(runtime, null, "PROJECT_DELETE_PROTECTED");
        await Reject(removed, null, "PROJECT_DELETE_RECENT");
        var ordinary = Path.Combine(root, "ordinary-folder");
        Directory.CreateDirectory(ordinary);
        await recent.RememberAsync("ordinary", ordinary);
        await Reject(ordinary, null, "PROJECT_DELETE_MARKER");
        var cancelled = await Fixture("cancelled");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                await recent.DeleteAsync(cancelled, null, cancellation.Token);
                throw new InvalidOperationException("Cancelled deletion accepted.");
            }
            catch (OperationCanceledException) { }
        }
        Check(File.Exists(Path.Combine(cancelled, "source.c")) && (await recent.LoadAsync()).Any(p => p.Directory == cancelled),
            "cancellation preserves project and recent entry");
        var recycled = await Fixture("recycle-" + Guid.NewGuid().ToString("N"));
        await recent.RememberAsync("same name", recycled + Path.DirectorySeparatorChar);
        Check((await recent.LoadAsync()).Count(p => Path.TrimEndingDirectorySeparator(p.Directory) == recycled) == 1,
            "trailing directory separator does not duplicate a recent project");
        await recent.DeleteAsync(recycled, null);
        Check(!Directory.Exists(recycled) && !(await recent.LoadAsync()).Any(p => p.Directory == recycled) &&
            File.Exists(Path.Combine(kept, "source.c")), "real Windows Shell recycles isolated fixture and removes only its record");
    }
}
