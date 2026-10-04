using System.Diagnostics;
using System.Text.Json;
using StudioX.Application.CodeIntelligence;

internal static class LiveDiagnosticChecks
{
    public static async Task RunAsync(string runtime, string fixture, string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            check(value, name);
            checks.Add(name);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        const string path = "src/live.c";
        const string headerPath = "include/live.h";
        const string header = "int live_value(void);\n";
        const string text = "#include \"live.h\"\nint live_use(void) { return live_value(); }\n";
        await File.WriteAllTextAsync(Path.Combine(fixture, path), text, token);
        await File.WriteAllTextAsync(Path.Combine(fixture, headerPath), header, token);
        static CodeDocumentSnapshot[] Workspace(string source, string dependency) => [new(path, source), new(headerPath, dependency)];
        async Task<CodeDiagnosticBatch> WaitBatch(CodeIntelligenceService service, string file, string source)
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (service.GetDiagnostics().FirstOrDefault(b => b.Path == file && b.Text == source) is { } batch)
                {
                    return batch;
                }
                await Task.Delay(40, token);
            }
            throw new InvalidOperationException("No fresh batch for " + file + "\n" + string.Join('\n', service.DrainLog()));
        }
        await using (var service = new CodeIntelligenceService(runtime, Path.Combine(output, "real-data")))
        {
            await service.StartAsync(fixture, token);
            await service.SynchronizeDiagnosticsAsync(path, text, Workspace(text, header), token);
            Check((await WaitBatch(service, path, text)).Items.All(i => i.Severity != 1), "real clangd starts with valid nested header and source");
            service.InvalidateDiagnostics();
            Check(service.GetDiagnostics().Count == 0, "editing a dependency immediately revokes unchanged source diagnostics before debounce");
            var badHeader = "int changed_name(void);\n";
            await service.SynchronizeDiagnosticsAsync(path, text, Workspace(text, badHeader), token);
            Check((await WaitBatch(service, path, text)).Items.Any(i => i.Severity == 1 && i.Message.Contains("live_value", StringComparison.Ordinal)), "unsaved header edits produce fresh dependent source error");
            service.InvalidateDiagnostics();
            await service.SynchronizeDiagnosticsAsync(path, text, [new(path, text)], token);
            Check((await WaitBatch(service, path, text)).Items.All(i => i.Severity != 1), "closing dirty header restores disk dependency and clears its unsaved error");
            for (var edit = 0; edit < 6; edit++)
            {
                var broken = text.Replace("live_value()", "unknown_" + edit + "()", StringComparison.Ordinal);
                service.InvalidateDiagnostics();
                await service.SynchronizeDiagnosticsAsync(path, broken, Workspace(broken, header), token);
                Check((await WaitBatch(service, path, broken)).Items.Any(i => i.Message.Contains("unknown_" + edit, StringComparison.Ordinal)), "rapid edit cycle " + edit + " retains actual new error");
                service.InvalidateDiagnostics();
                await service.SynchronizeDiagnosticsAsync(path, text, Workspace(text, header), token);
                Check((await WaitBatch(service, path, text)).Items.All(i => i.Severity != 1), "rapid edit cycle " + edit + " clears old error with a fresh clean batch");
            }
            await File.WriteAllLinesAsync(Path.Combine(output, "real-clangd.log"), service.DrainLog(), token);
        }
        // 仅复制本次验证程序到隔离 runtime，协议故障不作用于安装中的 clangd。
        var fakeRuntime = Path.Combine(output, "fixture-runtime");
        var bin = Path.Combine(fakeRuntime, "languages/clangd/bin");
        Directory.CreateDirectory(bin);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
        }
        File.Copy(Environment.ProcessPath!, Path.Combine(bin, "clangd.exe"));
        await using (var service = new CodeIntelligenceService(fakeRuntime, Path.Combine(output, "fixture-data")))
        {
            await service.StartAsync(fixture, token);
            var broken = "// 😀 中文\r\nint probe(void) { return FAKE_REAL_ERROR; }\r\n// FAKE_BAD_RANGE FAKE_OLD_VERSION FAKE_FUTURE_VERSION FAKE_NO_VERSION\r\n";
            await service.SynchronizeDiagnosticsAsync(path, broken, [new(path, broken)], token);
            var batch = await WaitBatch(service, path, broken);
            Check(batch.Items.Count == 1 && batch.Items[0].Message == "FAKE_REAL_ERROR", "protocol fixture rejects old/future/unversioned batches and retains valid item beside bad ranges");
            Check(!batch.IsComplete, "malformed diagnostic data is marked incomplete instead of claiming a clean analysis");
            Check(CodePositions.ToOffset(broken, batch.Items[0].Range.Start) == broken.IndexOf("FAKE_REAL_ERROR", StringComparison.Ordinal), "CRLF, Chinese and surrogate pairs retain UTF-16 diagnostic location");
            var log = service.DrainLog().ToArray();
            Check(log.Any(l => l.Contains("OUT_OF_RANGE", StringComparison.Ordinal)) && log.Any(l => l.Contains("NULL_RANGE", StringComparison.Ordinal)), "invalid range original JSON and exception are retained");
            await File.WriteAllLinesAsync(Path.Combine(output, "invalid-ranges.log"), log, token);
            service.InvalidateDiagnostics();
            var slow = "int probe(void) { return FAKE_REAL_ERROR; } // FAKE_DELAY\n";
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pending = service.SynchronizeDiagnosticsAsync(path, slow, [new(path, slow)], cancelled.Token);
            var waiting = Path.Combine(fixture, ".fixture-waiting");
            while (!File.Exists(waiting))
            {
                await Task.Delay(20, token);
            }
            var stale = "int probe(void) { return FAKE_REAL_ERROR; } // superseded\n";
            var queued = service.SynchronizeDiagnosticsAsync(path, stale, [new(path, stale)], token);
            service.InvalidateDiagnostics();
            cancelled.Cancel();
            var clean = "int probe(void) { return 0; }\n";
            var latest = service.SynchronizeDiagnosticsAsync(path, clean, [new(path, clean)], token);
            try
            {
                await pending;
                throw new InvalidOperationException("Expected cancellation");
            }
            catch (OperationCanceledException) { }
            Check(service.GetDiagnostics().Count == 0, "cancelled partial dependency synchronization cannot publish candidate or delayed diagnostics");
            await queued;
            Check(service.GetDiagnostics().All(b => b.Text != stale), "queued analysis captured before a later edit cannot install its superseded snapshot");
            await latest;
            Check((await WaitBatch(service, path, clean)).Items.Count == 0, "next edit recovers after cancellation without reviving previous error");
            var projectManifest = Path.Combine(fixture, ".studiox/project.json");
            var manifestBytes = await File.ReadAllBytesAsync(projectManifest, token);
            try
            {
                await File.WriteAllTextAsync(Path.Combine(fixture, ".fixture-init-delay"), "delay", token);
                await File.AppendAllTextAsync(projectManifest, "\n", token);
                var loadingOld = service.SynchronizeDiagnosticsAsync(path, stale, [new(path, stale)], token);
                while (!File.Exists(Path.Combine(fixture, ".fixture-initializing")))
                {
                    await Task.Delay(20, token);
                }
                service.InvalidateDiagnostics();
                var loadingLatest = service.SynchronizeDiagnosticsAsync(path, clean, [new(path, clean)], token);
                await loadingOld;
                Check(service.GetDiagnostics().All(b => b.Text != stale), "edit during configuration reload prevents older captured workspace from publishing in new session");
                await loadingLatest;
                Check((await WaitBatch(service, path, clean)).Items.Count == 0, "latest queued workspace survives a simultaneous configuration reload");
            }
            finally
            {
                File.Delete(Path.Combine(fixture, ".fixture-init-delay"));
                await File.WriteAllBytesAsync(projectManifest, manifestBytes, CancellationToken.None);
            }
            await service.RefreshEnvironmentAsync(token);
            async Task KillOwnedServer()
            {
                var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, ".fixture-pid"), token));
                using var process = Process.GetProcessById(pid);
                if (!string.Equals(process.MainModule?.FileName, Path.GetFullPath(Path.Combine(bin, "clangd.exe")), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Unexpected server identity: " + process.MainModule?.FileName);
                }
                process.Kill();
                await process.WaitForExitAsync(token);
                Check(!service.IsReady && service.GetDiagnostics().Count == 0, "own protocol process exit immediately hides cached diagnostics");
            }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await KillOwnedServer();
                Check(await service.RefreshEnvironmentAsync(token) && service.IsReady, "unchanged configuration restarts failed server attempt " + (attempt + 1));
                await service.SynchronizeDiagnosticsAsync(path, clean, [new(path, clean)], token);
                Check((await WaitBatch(service, path, clean)).Items.Count == 0, "restarted session receives new document and publishes clean batch " + (attempt + 1));
            }
            await KillOwnedServer();
            Check(!await service.RefreshEnvironmentAsync(token) && !service.IsReady && service.StatusDescription.Contains("一分钟", StringComparison.Ordinal), "fourth consecutive exit pauses automatic restart instead of spinning");
            await service.StartAsync(fixture, token);
            Check(service.IsReady, "explicit restart can recover before automatic retry cooldown expires");
            try
            {
                var badProtocol = "// FAKE_BAD_PROTOCOL\n";
                await service.SynchronizeDiagnosticsAsync(path, badProtocol, [new(path, badProtocol)], token);
                throw new InvalidOperationException("Expected malformed protocol rejection");
            }
            catch (JsonException) { }
            Check(!service.IsReady && service.GetDiagnostics().Count == 0, "broken framing clears readiness even while child process remains alive");
            Check(await service.RefreshEnvironmentAsync(token) && service.IsReady, "broken reader channel is disposed and restarted without reopening project");
            await service.SynchronizeDiagnosticsAsync(path, clean, [new(path, clean)], token);
            Check((await WaitBatch(service, path, clean)).Items.Count == 0, "protocol recovery reanalyzes current text");
            await File.WriteAllLinesAsync(Path.Combine(output, "process-recovery.log"), service.DrainLog(), token);
        }
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            passed = true,
            hardware = false,
            firmwareBuild = false,
            realClangd = true,
            protocolFailureFixture = true,
            checks
        }, new JsonSerializerOptions { WriteIndented = true }), token);
    }
}
