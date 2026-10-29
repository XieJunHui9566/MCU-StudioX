using System.Security.Cryptography;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

internal static class QualifiedChecks
{
    private sealed class Output(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
    internal static async Task RunAsync(string directory, bool lifecycleOnly = false)
    {
        var root = Path.GetFullPath(directory);
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture.json")))!;
        var catalog = new ToolsetCatalog(fixture["tools"]!.GetValue<string>());
        var builder = new BuildService(catalog);
        var checks = new List<string>();
        var logs = Path.Combine(root, lifecycleOnly ? "ide-lifecycle-checks" : "ide-checks");
        Directory.CreateDirectory(logs);
        async Task Check(bool success, string label)
        {
            if (!success)
            {
                throw new InvalidOperationException(label);
            }
            Console.WriteLine("PASS " + label);
            checks.Add(label);
            // 每条进度追加独立记录；最终汇总只原子发布一次，避免密集替换同一文件。
            await File.AppendAllTextAsync(Path.Combine(logs, "progress.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    label,
                    passed = true
                }) + "\n");
        }
        string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
        async Task Build(string project, string label, string? hash = null)
        {
            var report = await builder.BuildAsync(project);
            await File.WriteAllTextAsync(Path.Combine(logs, label + ".log"), report.Log);
            await Check(report.Success, label + " build");
            if (hash is not null)
            {
                await Check(Hash(Path.Combine(project, ".build/firmware.hex")) == hash, label + " exact sealed HEX");
            }
            await Check(File.ReadAllBytes(Path.Combine(project, ".build/firmware.bin")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(project, ".build/mx251/latest/firmware.bin"))), label + " BIN publication");
        }
        async Task Empty(string project, string label)
        {
            foreach (var p in new[] { ".build/firmware.hex", ".build/firmware.bin", ".build/studiox-build-receipt.json", ".build/mx251/latest/firmware.hex", ".build/mx251/latest/firmware.bin", ".build/mx251/latest/linked.partial.hex", ".build/mx251/latest/receipt.json" })
            {
                await Check(!File.Exists(Path.Combine(project, p)), label + " absent " + p);
            }
        }
        foreach (var c in lifecycleOnly ? Array.Empty<JsonNode?>() : fixture["cases"]!.AsArray().ToArray())
        {
            var id = c!["id"]!.GetValue<string>();
            var project = Path.Combine(root, "projects/ide", id);
            var cmakePath = Path.Combine(project, "CMakeLists.txt");
            var cmake = await File.ReadAllTextAsync(cmakePath);
            await Build(project, id + "-off", c["off"]!.GetValue<string>());
            var memory = new BuildMemoryService(catalog);
            var usage = await memory.ReadAsync(project);
            await JsonStore.WriteAsync(Path.Combine(logs, id + "-memory.json"), usage);
            await Check(usage.Targets.Count > 0 && usage.Targets.All(t => t.Diagnostic is null), id + " memory analyzer");
            await new BuildHistoryService(Path.Combine(root, "user-data"), memory).CaptureAsync(project);
            await Check(true, id + " history captured");
            await File.WriteAllTextAsync(cmakePath, cmake.Replace("include(", "set(MX251_LEAF_CALL_FEEDBACK ON CACHE BOOL \"explicit project choice\" FORCE)\ninclude("));
            await Build(project, id + "-on", c["on"]!.GetValue<string>());
            await File.WriteAllTextAsync(cmakePath, cmake);
            await Build(project, id + "-off-again", c["off"]!.GetValue<string>());
        }
        var sample = fixture["cases"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "calls-small")!;
        var pRoot = Path.Combine(root, "projects/ide/calls-small");
        var cmakeFile = Path.Combine(pRoot, "CMakeLists.txt");
        var originalCmake = await File.ReadAllTextAsync(cmakeFile);
        var main = Path.Combine(pRoot, "main.c");
        var originalMain = await File.ReadAllTextAsync(main);
        var configPath = Path.Combine(pRoot, "mx251.json");
        var originalConfig = await File.ReadAllTextAsync(configPath);
        async Task Failure(string label, string expected)
        {
            var report = await builder.BuildAsync(pRoot);
            await File.WriteAllTextAsync(Path.Combine(logs, label + ".log"), report.Log);
            await Check(!report.Success && report.Log.Contains(expected, StringComparison.OrdinalIgnoreCase), label + " raw diagnostic");
            await Empty(pRoot, label);
        }
        await File.WriteAllTextAsync(cmakeFile, originalCmake + "\nmessage(FATAL_ERROR \"EXPECTED_CONFIGURE_FAILURE\")\n");
        await Failure("configure-failure", "EXPECTED_CONFIGURE_FAILURE");
        await File.WriteAllTextAsync(cmakeFile, originalCmake);
        await Build(pRoot, "configure-recovery");
        await File.WriteAllTextAsync(main, "#error EXPECTED_COMPILE_FAILURE\n" + originalMain);
        await Failure("compile-failure", "EXPECTED_COMPILE_FAILURE");
        await File.WriteAllTextAsync(main, originalMain);
        await Build(pRoot, "compile-recovery");
        await File.WriteAllTextAsync(main, originalMain);
        // 固定语料的格式可能不同；独立 TU 强制制造不能被 DCE 删除的未解析引用。
        await File.WriteAllTextAsync(Path.Combine(pRoot, "missing.c"), "extern unsigned char missing_link_variable; void missing_link_function(void) { missing_link_variable=1; }\n");
        await File.WriteAllTextAsync(cmakeFile, originalCmake.Replace("mx251_firmware(app)", "target_sources(app INTERFACE missing.c)\nmx251_firmware(app)"));
        await Failure("link-failure", "missing_link_variable");
        File.Delete(Path.Combine(pRoot, "missing.c"));
        await File.WriteAllTextAsync(main, originalMain);
        await File.WriteAllTextAsync(cmakeFile, originalCmake);
        await Build(pRoot, "link-recovery");
        var config = JsonNode.Parse(originalConfig)!;
        config["target"] = "AI8051U-34K64-32";
        await File.WriteAllTextAsync(configPath, config.ToJsonString());
        try
        {
            await builder.BuildAsync(pRoot);
            throw new InvalidOperationException("Wrong target accepted");
        }
        catch (StudioXException e) when (e.Code == "MX251_TARGET") { await Check(true, "target mismatch rejected"); }
        await Empty(pRoot, "target mismatch");
        await File.WriteAllTextAsync(configPath, originalConfig);
        await Build(pRoot, "target-recovery");
        var before = Hash(Path.Combine(pRoot, ".build/firmware.hex"));
        using (var lease = new FileStream(Path.Combine(pRoot, ".build/mx251/build.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            try
            {
                await builder.BuildAsync(pRoot);
                throw new InvalidOperationException("Concurrent CLI lock ignored");
            }
            catch (StudioXException e) when (e.Code == "MX251_BUSY") { await Check(Hash(Path.Combine(pRoot, ".build/firmware.hex")) == before, "active CLI lease preserves output"); }
        }
        using (var cancel = new CancellationTokenSource())
        {
            await File.WriteAllTextAsync(cmakeFile, originalCmake + "\nmessage(STATUS \"MX251_CONFIGURE_CANCEL\")\nexecute_process(COMMAND \"${CMAKE_COMMAND}\" -E sleep 20)\n");
            try
            {
                await builder.BuildAsync(pRoot, cancellationToken: cancel.Token, output: new Output(line => { if (line.Contains("MX251_CONFIGURE_CANCEL")) { cancel.Cancel(); } }));
                throw new InvalidOperationException("Configure cancellation ignored");
            }
            catch (OperationCanceledException) { await Check(cancel.IsCancellationRequested, "configure child process cancelled"); }
        }
        await Empty(pRoot, "configure cancelled");
        await File.WriteAllTextAsync(cmakeFile, originalCmake);
        await Build(pRoot, "configure-cancel-recovery");
        await File.WriteAllTextAsync(cmakeFile, originalCmake.Replace("include(", "set(MX251_LEAF_CALL_FEEDBACK ON)\ninclude("));
        using (var cancel = new CancellationTokenSource())
        {
            var task = builder.BuildAsync(pRoot, cancellationToken: cancel.Token);
            // 等待第二遍真实编译的原始日志；不对生产入口加入测试睡眠或假编译器。
            var deadline = DateTime.UtcNow.AddMinutes(3);
            var cancelled = false;
            while (!task.IsCompleted && DateTime.UtcNow < deadline)
            {
                var dir = Path.Combine(pRoot, ".build/mx251/latest");
                if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "feedback-*.stdout.log").Any())
                {
                    cancelled = true;
                    cancel.Cancel();
                    break;
                }
                await Task.Delay(20);
            }
            if (!cancelled)
            {
                cancel.Cancel();
            }
            try
            {
                await task;
                throw new InvalidOperationException("Expected cancelled compilation");
            }
            catch (OperationCanceledException) { await Check(cancelled, "cancelled actual second pass"); }
        }
        await Empty(pRoot, "cancelled");
        await Check(File.Exists(Path.Combine(pRoot, ".build/mx251/latest/owner.json")), "cancelled directory ownership retained");
        await Build(pRoot, "cancel-recovery", sample["on"]!.GetValue<string>());
        await File.WriteAllTextAsync(cmakeFile, originalCmake);
        await Build(pRoot, "final-default", sample["off"]!.GetValue<string>());
        await JsonStore.WriteAsync(Path.Combine(logs, "results.json"), new
        {
            checks,
            hardware = false,
            complete = true
        });
    }
}
