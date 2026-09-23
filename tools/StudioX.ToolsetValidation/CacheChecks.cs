using System.Security.Cryptography;
using System.Text;
using StudioX.Engine;
using StudioX.Foundation;

internal static class CacheChecks
{
    public static async Task<int> RunAsync(string destination)
    {
        var root = Path.GetFullPath(destination);
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a new check directory.");
        var toolsRoot = Path.Combine(root, "toolsets");
        var tree = Path.Combine(toolsRoot, "test.tools", "1.0.0");
        Directory.CreateDirectory(Path.Combine(tree, "bin"));
        Directory.CreateDirectory(Path.Combine(tree, "support"));
        var roles = new[] { "cmake", "ninja", "gcc", "gxx", "objcopy", "size" }
            .ToDictionary(role => role, role => "bin/" + role + ".exe", StringComparer.Ordinal);
        var contents = roles.Values.Append("support/runtime.dll").Append("support/target.cfg")
            .Append("gcc/share/gdb/python/gdb/command/frame_filters.py")
            .ToDictionary(path => path, path => Encoding.UTF8.GetBytes("Synthetic verification fixture; never executed: " + path));
        foreach (var (relative, bytes) in contents)
        {
            var path = Path.Combine(tree, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }
        var manifest = new ToolsetManifest(1, "test.tools", "1.0.0", "win-x64", "test-compiler", roles,
            contents.ToDictionary(pair => pair.Key, pair => Convert.ToHexString(SHA256.HashData(pair.Value))),
            ResourceDirectories: new() { ["openocdScripts"] = "support" });
        var manifestPath = Path.Combine(tree, "toolset.json");
        await JsonStore.WriteAsync(manifestPath, manifest);
        var catalog = new ToolsetCatalog(toolsRoot);
        var events = new List<string>();
        var progress = new InlineProgress(events.Add);
        var passed = new List<string>();
        async Task<ResolvedToolset> Resolve(bool force = false, CancellationToken token = default) =>
            await catalog.ResolveAsync("test.tools", "1.0.0", "test-compiler", token, forceVerification: force, progress: progress);
        bool Full() => events.Any(line => line.StartsWith("完整校验", StringComparison.Ordinal));
        bool Cached() => events.Any(line => line.Contains("复用", StringComparison.Ordinal));
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); passed.Add(name); events.Clear(); }
        async Task Reject(string code, Func<Task> action)
        {
            try { await action(); throw new InvalidOperationException("Expected " + code); }
            catch (StudioXException error) when (error.Code == code) { passed.Add("rejected " + code); events.Clear(); }
        }
        await Resolve(); Check(Full() && !Cached(), "first resolve verifies all content");
        await Resolve(); Check(Cached() && !Full(), "unchanged resolve reuses successful result");
        Directory.SetLastWriteTimeUtc(Path.Combine(tree, "support"), DateTime.UtcNow.AddMinutes(-1));
        await Resolve(); Check(Cached() && !Full(), "directory timestamp change does not invalidate verified files");
        await Resolve(force: true); Check(Full() && !Cached(), "manual verification bypasses cache");
        await Reject("TOOLSET_INCOMPATIBLE", () => catalog.ResolveAsync("test.tools", "1.0.0", "wrong-compiler"));
        await Resolve(); events.Clear();

        var library = Path.Combine(tree, "support/runtime.dll");
        File.SetLastWriteTimeUtc(library, File.GetLastWriteTimeUtc(library).AddSeconds(2));
        await Resolve(); Check(Full() && !Cached(), "timestamp change forces full verification");
        var original = contents["support/runtime.dll"];
        var corrupt = original.ToArray(); corrupt[0] ^= 0x20;
        await File.WriteAllBytesAsync(library, corrupt);
        File.SetLastWriteTimeUtc(library, DateTime.UtcNow.AddSeconds(4));
        await Reject("TOOL_HASH", () => Resolve());
        await File.WriteAllBytesAsync(library, original);
        await Resolve(); Check(Full(), "failure cannot leave reusable result");

        var script = Path.Combine(tree, "support/target.cfg");
        File.Move(script, Path.Combine(root, "target.cfg.saved"));
        await Reject("TOOL_HASH", () => Resolve());
        File.Move(Path.Combine(root, "target.cfg.saved"), script);
        await Resolve(); events.Clear();
        var extra = Path.Combine(tree, "unexpected.dll");
        await File.WriteAllTextAsync(extra, "extra");
        await Reject("TOOL_HASH", () => Resolve());
        File.Move(extra, Path.Combine(root, "unexpected.dll.saved"));
        await Resolve(); events.Clear();

        var cacheDirectory = Path.Combine(tree, "gcc/share/gdb/python/gdb/command/__pycache__");
        Directory.CreateDirectory(cacheDirectory);
        var generated = Path.Combine(cacheDirectory, "frame_filters.cpython-39.pyc");
        await File.WriteAllTextAsync(generated, "generated bytecode fixture");
        await Resolve(); Check(Full() && !Cached(), "indexed GDB Python source permits generated bytecode cache");
        await Resolve(); Check(Cached(), "stable GDB Python cache does not force rehash");
        var unindexedCache = Path.Combine(cacheDirectory, "unindexed.cpython-39.pyc");
        await File.WriteAllTextAsync(unindexedCache, "unexpected bytecode fixture");
        await Reject("TOOL_HASH", () => Resolve());
        File.Move(unindexedCache, Path.Combine(root, "unindexed.pyc.saved"));
        var wrongCache = Path.Combine(cacheDirectory, "frame_filters.pyc");
        await File.WriteAllTextAsync(wrongCache, "unexpected legacy bytecode fixture");
        await Reject("TOOL_HASH", () => Resolve());
        File.Move(wrongCache, Path.Combine(root, "legacy.pyc.saved"));
        await Resolve(); events.Clear();

        var environmentA = ToolsetEnvironment.Create(await Resolve());
        var environmentB = ToolsetEnvironment.Create(await Resolve());
        Check(environmentA["PYTHONDONTWRITEBYTECODE"] == "1" && environmentA["PYTHONPYCACHEPREFIX"] != environmentB["PYTHONPYCACHEPREFIX"]
            && !Path.GetFullPath(environmentA["PYTHONPYCACHEPREFIX"]).StartsWith(tree + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "bundled processes isolate Python cache outside toolset");

        var before = await Resolve(); events.Clear();
        await JsonStore.WriteAsync(manifestPath, manifest with { DisplayName = "updated manifest" });
        var after = await Resolve();
        Check(Full() && after.Fingerprint != before.Fingerprint, "manifest change invalidates result and fingerprint");
        var plainJson = await File.ReadAllBytesAsync(manifestPath);
        await File.WriteAllBytesAsync(manifestPath, [0xef, 0xbb, 0xbf, ..plainJson]);
        await Resolve(); Check(Full(), "UTF-8 BOM manifest remains supported");

        using (var cancel = new CancellationTokenSource())
        {
            var cancelProgress = new InlineProgress(line => { if (line.StartsWith("完整校验", StringComparison.Ordinal)) cancel.Cancel(); });
            try { await catalog.ResolveAsync("test.tools", "1.0.0", "test-compiler", cancel.Token, true, cancelProgress); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { passed.Add("cancellation interrupts complete verification"); }
        }
        await Resolve(); Check(Full(), "cancelled verification does not populate cache");
        var changingProgress = new InlineProgress(line =>
        {
            if (line.StartsWith("完整校验", StringComparison.Ordinal)) File.SetLastWriteTimeUtc(script, DateTime.UtcNow.AddSeconds(8));
        });
        await Reject("TOOL_CHANGED", () => catalog.ResolveAsync("test.tools", "1.0.0", "test-compiler", default, true, changingProgress));
        await Resolve(); Check(Full(), "changed-during-verify result is not cached");

        // 手动完整检查仍读取内容，即使文件被工具保留了相同的长度和时间。
        var stamp = File.GetLastWriteTimeUtc(library);
        await File.WriteAllBytesAsync(library, corrupt); File.SetLastWriteTimeUtc(library, stamp);
        await Reject("TOOL_HASH", () => Resolve(force: true));
        await File.WriteAllBytesAsync(library, original);
        var fresh = new ToolsetCatalog(toolsRoot);
        await fresh.ResolveAsync("test.tools", "1.0.0", "test-compiler", progress: progress);
        Check(Full() && !Cached(), "new catalog/process performs complete verification");
        await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), passed.Select(name => "PASS: " + name));
        Console.WriteLine($"PASS: {passed.Count} cache and failure checks. Synthetic files only; no tool or hardware execution.");
        return 0;
    }
}
