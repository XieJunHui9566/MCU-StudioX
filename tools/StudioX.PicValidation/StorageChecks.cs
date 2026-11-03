namespace StudioX.PicValidation;

using System.Diagnostics;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>在隔离目录验证输出清理边界、独占租约及调试恢复保留，不访问硬件。</summary>
internal static class StorageChecks
{
    public static async Task RunAsync(string evidence)
    {
        if (Directory.Exists(evidence))
        {
            throw new ArgumentException("Use a new evidence directory.");
        }
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        void Check(bool value, string label)
        {
            if (!value)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        void Reject(string code, Action action)
        {
            try
            {
                action();
                throw new InvalidOperationException("Expected " + code);
            }
            catch (StudioXException error) when (error.Code == code) { Check(true, code + " rejected"); }
        }
        string Project(string name)
        {
            var root = Path.Combine(evidence, name);
            Directory.CreateDirectory(root);
            return root;
        }
        string OldRun(string root, string area, string? name = null)
        {
            var path = Path.Combine(root, ".build", area, name ?? Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
        foreach (var (family, area) in new[] { ("pic", "pic"), ("pic", "pic-download"), ("pic", "pic-debug"),
            ("arduino", "arduino"), ("arduino", "arduino-upload"), ("ag32-logic", "ag32-logic") })
        {
            var root = Project(area);
            var first = OldRun(root, area);
            var second = OldRun(root, area);
            var user = OldRun(root, area, "user-notes");
            File.WriteAllText(Path.Combine(user, "keep.txt"), "user content");
            using var storage = ProjectBuildStorage.Acquire(root, family);
            var latest = storage.Prepare(area);
            File.WriteAllText(Path.Combine(latest, "stale.obj"), "previous output");
            Check(storage.Prepare(area) == latest && !File.Exists(Path.Combine(latest, "stale.obj")) &&
                !Directory.Exists(first) && !Directory.Exists(second) && File.Exists(Path.Combine(user, "keep.txt")) &&
                Directory.GetDirectories(Path.GetDirectoryName(latest)!).Length == 2, area + " rotates only latest and preserves user directories");
            Reject("PROJECT_OUTPUT_BUSY", () => { using var other = ProjectBuildStorage.Acquire(root, family); });
        }
        var download = Project("download-backup");
        using (var storage = ProjectBuildStorage.Acquire(download, "pic"))
        {
            var previous = OldRun(download, "pic-download");
            File.WriteAllText(Path.Combine(previous, "before.hex"), "original");
            File.WriteAllText(Path.Combine(previous, "download.log"), "diagnostic");
            var latest = storage.Prepare("pic-download");
            var backup = Path.Combine(download, ".build/pic-download/backup");
            Check(File.ReadAllText(Path.Combine(backup, "before.hex")) == "original" &&
                File.ReadAllText(Path.Combine(backup, "download.log")) == "diagnostic", "old download backup and raw log survive migration");
            storage.Prepare("pic-download");
            Check(File.ReadAllText(Path.Combine(backup, "before.hex")) == "original", "empty or failed attempt does not discard the last backup");
            File.WriteAllText(Path.Combine(latest, "before.hex"), "new original");
            storage.Prepare("pic-download");
            Check(File.ReadAllText(Path.Combine(backup, "before.hex")) == "new original" &&
                Directory.GetDirectories(Path.GetDirectoryName(latest)!).Length == 2, "download backup is replaced with the newest while storage remains bounded");
            try
            {
                storage.Prepare("arduino");
                throw new InvalidOperationException("Family mismatch accepted");
            }
            catch (ArgumentException) { Check(true, "lease cannot clean a different output family"); }
        }
        var debug = Project("debug-recovery");
        using (var storage = ProjectBuildStorage.Acquire(debug, "pic"))
        {
            var previous = OldRun(debug, "pic-debug");
            File.WriteAllText(Path.Combine(previous, "before.hex"), "unrestored");
            var latest = storage.Prepare("pic-debug");
            var recovery = Path.Combine(debug, ".build/pic-debug/recovery/before.hex");
            Check(File.ReadAllText(recovery) == "unrestored", "unconfirmed debug restoration is retained separately");
            File.WriteAllText(Path.Combine(latest, "before.hex"), "another original");
            File.WriteAllText(Path.Combine(latest, "restored.json"), "{\"verified\":false}");
            Reject("PIC_RECOVERY_REQUIRED", () => storage.Prepare("pic-debug"));
            Check(File.ReadAllText(recovery) == "unrestored" && File.ReadAllText(Path.Combine(latest, "before.hex")) == "another original",
                "second unresolved session cannot overwrite either recovery copy");
            File.WriteAllText(Path.Combine(latest, "restored.json"), "{\"verified\":true}");
            storage.Prepare("pic-debug");
            Check(File.ReadAllText(Path.Combine(debug, ".build/pic-debug/backup/before.hex")) == "another original" &&
                File.ReadAllText(recovery) == "unrestored", "verified debug session rotates to one backup without touching unresolved recovery");
            File.WriteAllText(Path.Combine(latest, "before.hex"), "invalid-record original");
            File.WriteAllText(Path.Combine(latest, "restored.json"), "invalid JSON");
            Reject("PIC_RECOVERY_REQUIRED", () => storage.Prepare("pic-debug"));
            Check(File.Exists(Path.Combine(latest, "before.hex")), "malformed restoration record preserves its original directory");
        }
        var links = Project("link-boundary");
        var outside = Project("outside");
        var sentinel = Path.Combine(outside, "keep.txt");
        File.WriteAllText(sentinel, "untouched");
        var linkedRun = OldRun(links, "pic");
        // Windows 目录联接无需管理员权限；由系统工具创建真实的重解析点夹具。
        var junction = Path.Combine(linkedRun, "external");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", junction, outside })
        {
            start.ArgumentList.Add(argument);
        }
        using (var process = Process.Start(start)!)
        {
            await process.WaitForExitAsync();
            Check(process.ExitCode == 0, "real junction fixture created");
        }
        using (var storage = ProjectBuildStorage.Acquire(links, "pic"))
        {
            Reject("PATH_LINK", () => storage.Prepare("pic"));
            Check(Directory.Exists(linkedRun) && File.ReadAllText(sentinel) == "untouched", "linked output is rejected before any recursive deletion");
        }
        Directory.Delete(junction);
        var lease = ProjectBuildStorage.Acquire(links, "pic");
        lease.Dispose();
        using (ProjectBuildStorage.Acquire(links, "pic"))
        {
            Check(true, "disposed lease can be acquired again");
        }
        await JsonStore.WriteAsync(Path.Combine(evidence, "result.json"), new
        {
            passed = true,
            hardware = false,
            checks
        });
    }
}
