using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Editing;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args.Any(a => a.StartsWith("--compile-commands-dir=", StringComparison.Ordinal)))
{
    await DiagnosticServerFixture.RunAsync();
    return;
}

if (args is ["--verify-project-analysis", var analysisRuntime, var analysisProject, var analysisOutput])
{
    var verified = new List<string>();
    await ProjectAnalysisChecks.RunAsync(Path.GetFullPath(analysisRuntime), Path.GetFullPath(analysisProject), Path.GetFullPath(analysisOutput), (value, message) =>
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
        verified.Add(message);
        Console.WriteLine("PASS " + message);
    });
    await File.WriteAllLinesAsync(Path.Combine(analysisOutput, "result.txt"), verified);
    return;
}

if (args.Length != 2) { throw new ArgumentException("runtime directory, new output directory required"); }
var runtime = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) { throw new IOException("Use a new output directory."); }
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
    checks.Add(message);
    Console.WriteLine("PASS " + message);
}
async Task Reject(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (Exception ex) when (ex is StudioXException or ArgumentException or IOException or OperationCanceledException)
    {
        Check(true, message);
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + message);
}
DiagnosticTextChecks.Run(Check);
var project = Path.Combine(root, "中文工程 with spaces");
Directory.CreateDirectory(Path.Combine(project, "src"));
Directory.CreateDirectory(Path.Combine(project, "include"));
Directory.CreateDirectory(Path.Combine(project, ".build"));
Directory.CreateDirectory(Path.Combine(project, "device"));
Directory.CreateDirectory(Path.Combine(project, ".studiox"));
await File.WriteAllTextAsync(Path.Combine(project, "src/main.c"), "// 中文 😀\r\nint value = 1;\r\nint main(void) { return value; }\r\n");
await File.WriteAllTextAsync(Path.Combine(project, "src/other.c"), "int value_other = 2;\nint value = 3;\n");
await File.WriteAllTextAsync(Path.Combine(project, ".build/ignored.c"), "value");
await File.WriteAllTextAsync(Path.Combine(project, "device/ignored.c"), "value");
await File.WriteAllBytesAsync(Path.Combine(project, "binary.dat"), [0, 1, 2]);
var files = new ProjectFileService();
var editing = new WorkspaceEditService(files);
var original = await files.ReadAsync(project, "src/main.c");
var dirty = original.Text + "// value draft\r\n";
WorkspaceBufferSnapshot[] buffers = [new(original, dirty)];
var search = await editing.SearchAsync(project, new("value", true, true, false), "counter", "*", "", false, buffers);
Check(search.Files.Count == 2 && search.Files.Sum(f => f.Matches.Count) == 4, "project search includes unsaved text, whole words, excludes build and device");
Check(search.Notices.Count == 1 && search.Files.Single(f => f.WasOpen).After.Contains("counter draft"), "binary skip observable; dirty snapshot used for preview");
Check((await File.ReadAllTextAsync(Path.Combine(project, "src/main.c"))) == original.Text, "preview never writes project files");
await editing.ValidateAsync(project, search.Files, buffers);
Check(true, "complete unchanged plan validates");
await Reject(() => editing.ValidateAsync(project, search.Files, [new(original, dirty + "x")]), "new unsaved edits reject complete stale plan");
await Reject(() => editing.ValidateAsync(project, search.Files, []), "closing a dirty source invalidates preview");
await File.AppendAllTextAsync(Path.Combine(project, "src/other.c"), "// concurrent\n");
await Reject(() => editing.ValidateAsync(project, search.Files, buffers), "external disk change rejects complete plan");
var included = await editing.SearchAsync(project, new("value", false, false, false), null, "src/*.c", "*other*", false, buffers);
Check(included.Files.Count == 1 && included.Files[0].Path == "src/main.c", "include and exclude globs constrain project search");
var regex = await editing.SearchAsync(project, new(@"(value)_other", true, false, true), "$1_new", "src/other.c", "", false, []);
Check(regex.Files[0].After.Contains("value_new"), "regex replacement captures preserved");
await Reject(() => editing.SearchAsync(project, new("[", false, false, true), "x", "*", "", false, []), "invalid regex fails before mutation");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await Reject(() => editing.SearchAsync(project, new("value", false, false, false), null, "*", "", false, [], cancelled.Token), "cancelled search stops");
}
Check(WorkspaceEditService.ApplyText("😀abc", [new(2, 3, "x")]) == "😀x", "edits use UTF-16 offsets");
await Reject(() => Task.FromResult(WorkspaceEditService.ApplyText("abc", [new(0, 2, "x"), new(1, 1, "y")])), "overlapping edits rejected");
await Reject(() => editing.ValidateAsync(project, [search.Files[0] with { Source = original with { RelativePath = "../outside.c" } }], buffers), "edit path escape rejected");

var data = Path.Combine(root, "user-data");
var stored = new StoredEditorDocument("src/main.c", dirty, original.Text, original.DiskHash, 65001, false, false, 8, 3, 2, 40, 0);
var snapshot = new EditorWorkspaceSnapshot(1, project, "src/main.c", [stored], DateTimeOffset.UtcNow);
using (var active = new EditorSessionStore(data))
{
    await active.SaveAsync(snapshot);
    using var second = new EditorSessionStore(data);
    Check(await second.ClaimLatestAsync() is null, "running editor lease prevents another instance from consuming drafts");
}
using (var recovering = new EditorSessionStore(data))
{
    var claimed = await recovering.ClaimLatestAsync();
    Check(claimed is not null && claimed.Documents[0].Draft == dirty, "released/crashed session can be claimed without saving source");
    using (var another = new EditorSessionStore(data))
    {
        Check(await another.ClaimLatestAsync() is null, "in-progress recovery keeps original record exclusively leased");
    }
    var restored = (await recovering.RestoreDocumentsAsync(claimed!)).Single();
    Check(restored.Text == dirty && restored.Source.Text == original.Text && restored.View.Caret == 8 && restored.View.SelectionLength == 2, "draft, baseline, caret and selection roundtrip");
    recovering.CompleteRecovery();
    await File.AppendAllTextAsync(Path.Combine(project, "src/main.c"), "// external change\n");
    restored = (await recovering.RestoreDocumentsAsync(snapshot)).Single();
    Check(restored.Notice is not null && restored.Source.DiskHash == original.DiskHash, "recovery exposes disk conflict and retains original save baseline");
    await Reject(() => files.SaveAsync(project, restored.Source, restored.Text), "recovered draft cannot overwrite concurrent disk change");
    await File.WriteAllBytesAsync(Path.Combine(project, "src/main.c"), [0, 255, 1]);
    restored = (await recovering.RestoreDocumentsAsync(snapshot)).Single();
    Check(restored.Text == dirty && !restored.Source.IsMissing && restored.Notice is not null, "external binary file does not prevent recovery of unsaved draft");
    await Reject(() => files.SaveAsync(project, restored.Source, restored.Text), "binary replacement still protected by saved disk hash");
    File.Delete(Path.Combine(project, "src/main.c"));
    restored = (await recovering.RestoreDocumentsAsync(snapshot)).Single();
    Check(restored.Source.IsMissing && restored.Text == dirty, "deleted original retains editable recovered draft");
    var saved = await files.SaveAsync(project, restored.Source, restored.Text);
    Check(!saved.IsMissing && await File.ReadAllTextAsync(Path.Combine(project, "src/main.c")) == dirty, "explicit save recreates missing original");
    var alreadySaved = (await recovering.RestoreDocumentsAsync(snapshot)).Single();
    Check(alreadySaved.Source.Text == dirty && alreadySaved.Source.DiskHash == saved.DiskHash, "draft already saved to disk restores as clean with current baseline");
    await Reject(() => files.SaveAsync(project, restored.Source, "do not overwrite"), "recreated file cannot be overwritten using missing-file baseline");
    await Reject(() => recovering.SaveAsync(snapshot with { Documents = [stored with { Path = "../escape.c" }] }), "recovery path traversal rejected");
    await Reject(() => recovering.SaveAsync(snapshot with { FormatVersion = 99 }), "unknown recovery format rejected");
}

var failedData = Path.Combine(root, "failed-recovery-data");
using (var beforeFailure = new EditorSessionStore(failedData)) { await beforeFailure.SaveAsync(snapshot); }
using (var failed = new EditorSessionStore(failedData))
{
    _ = await failed.ClaimLatestAsync();
    await failed.SaveAsync(snapshot with
    {
        Project = null,
        ActivePath = null,
        Documents = []
    });
}
using (var retry = new EditorSessionStore(failedData))
{
    Check((await retry.ClaimLatestAsync())?.Documents[0].Draft == dirty, "failed workspace restore cannot erase original draft when later state changes");
    retry.CompleteRecovery();
}

// 独立通用 Arm 夹具，仅启动内置 clangd，不执行 GCC、下载或设备连接。
var fixture = Path.Combine(root, "semantic-project");
Directory.CreateDirectory(Path.Combine(fixture, "src"));
Directory.CreateDirectory(Path.Combine(fixture, "include"));
Directory.CreateDirectory(Path.Combine(fixture, "device"));
Directory.CreateDirectory(Path.Combine(fixture, ".studiox"));
var device = new DeviceDefinition("fixture-arm", "Offline ARM fixture", "arm", 0x08000000, 131072, 0x20000000, 20480,
    "arm.gnu", "1.0.0", "arm-gnu-15.2.rel1", ["-mcpu=cortex-m3", "-mthumb"], [], [], [], "fixture.ld", [], [], [new("minimal", "Minimal", "Offline", "main.c")]);
await JsonStore.WriteAsync(Path.Combine(fixture, "device/manifest.json"), new PackManifest(1, "test.editor", "1.0.0", "Editor", "Test", [device]));
await JsonStore.WriteAsync(Path.Combine(fixture, ".studiox/project.json"), new ProjectManifest(1, "editor_fixture", "test.editor", "1.0.0", "offline", device.Id, "minimal", device.ToolsetId, device.ToolsetVersion, device.CompilerId));
var header = "extern int shared_value;\nint helper(void);\n";
var main = "#include \"shared.h\"\nint shared_value = 1;\nint main(void) { return shared_value + helper(); }\n";
var other = "#include \"shared.h\"\nint helper(void) { return shared_value; }\nint shadow(void) { int shared_value = 7; return shared_value; }\n";
await File.WriteAllTextAsync(Path.Combine(fixture, "include/shared.h"), header);
await File.WriteAllTextAsync(Path.Combine(fixture, "src/main.c"), main);
await File.WriteAllTextAsync(Path.Combine(fixture, "src/other.c"), other);
await using var intelligence = new CodeIntelligenceService(runtime, Path.Combine(root, "language-data"));
await intelligence.StartAsync(fixture);
CodeDocumentSnapshot[] documents = [new("src/main.c", main), new("src/other.c", other), new("include/shared.h", header)];
var refs = await intelligence.ReferencesAsync("src/main.c", main, main.IndexOf("shared_value", StringComparison.Ordinal), documents);
Check(refs.Count >= 4 && refs.Count(r => r.DocumentPath == "src/other.c") == 1, "real clangd references cross files and exclude shadowed variables");
var renameBuffers = await Task.WhenAll(documents.Select(async d => new WorkspaceBufferSnapshot(await files.ReadAsync(fixture, d.Path), d.Text)));
var rename = await intelligence.RenameAsync("src/main.c", main, main.IndexOf("shared_value", StringComparison.Ordinal), "shared_value", "shared_counter", renameBuffers);
Check(rename.Count == 3 && rename.Sum(r => r.Matches.Count) == 4, "real semantic rename plans declaration, definition and references");
Check(rename.Single(r => r.Path == "src/other.c").After.Contains("int shared_value = 7; return shared_value;"), "semantic rename preserves local shadow and unrelated names");
Check(await File.ReadAllTextAsync(Path.Combine(fixture, "src/main.c")) == main, "semantic rename remains a preview");
await Reject(() => intelligence.RenameAsync("src/main.c", main, 30, "shared_value", "123invalid", renameBuffers), "invalid rename identifier rejected");
var broken = main.Replace("return shared_value + helper()", "return unknown_symbol + helper()", StringComparison.Ordinal);
CodeDocumentSnapshot[] brokenWorkspace = [new("src/main.c", broken), new("src/other.c", other), new("include/shared.h", header)];
await intelligence.SynchronizeDiagnosticsAsync("src/main.c", broken, brokenWorkspace);
await intelligence.DocumentSymbolsAsync("src/main.c", broken, documents: brokenWorkspace);
for (var attempt = 0; attempt < 50 && !intelligence.GetDiagnostics().Any(b => b.Path == "src/main.c" && b.Text == broken && b.Items.Any(d => d.Severity == 1)); attempt++) { await Task.Delay(100); }
Check(intelligence.GetDiagnostics().Any(b => b.Path == "src/main.c" && b.Text == broken && b.Items.Any(d => d.Message.Contains("unknown_symbol", StringComparison.Ordinal))), "real-time diagnostics arrive for unsaved broken text");
await intelligence.DocumentSymbolsAsync("src/other.c", other, documents: brokenWorkspace);
Check(intelligence.GetDiagnostics().Any(b => b.Path == "src/main.c" && b.Items.Any(d => d.Message.Contains("unknown_symbol", StringComparison.Ordinal))), "refreshing another file does not erase current live error");
intelligence.SetDiagnosticsSuspended(true);
Check(intelligence.DiagnosticsSuspended && intelligence.GetDiagnostics().Count == 0, "debug suspension clears existing live diagnostics");
await intelligence.SynchronizeDiagnosticsAsync("src/main.c", broken, brokenWorkspace);
await intelligence.DocumentSymbolsAsync("src/main.c", broken + "\n", documents: [new("src/main.c", broken + "\n")]);
await Task.Delay(250);
Check(intelligence.GetDiagnostics().Count == 0, "navigation and late clangd publications cannot restore diagnostics during debug");
intelligence.SetDiagnosticsSuspended(false);
Check(intelligence.GetDiagnostics().Count == 0, "resuming does not revive stale cached diagnostics");
await intelligence.SynchronizeDiagnosticsAsync("src/main.c", broken, brokenWorkspace);
for (var attempt = 0; attempt < 50 && !intelligence.GetDiagnostics().Any(b => b.Text == broken && b.Items.Any(d => d.Message.Contains("unknown_symbol", StringComparison.Ordinal))); attempt++) { await Task.Delay(100); }
Check(!intelligence.DiagnosticsSuspended && intelligence.GetDiagnostics().Any(b => b.Text == broken && b.Items.Any(d => d.Message.Contains("unknown_symbol", StringComparison.Ordinal))), "diagnostics resume by reparsing unchanged source after debug");
await intelligence.SynchronizeDiagnosticsAsync("src/main.c", main, [new("src/main.c", main)]);
for (var attempt = 0; attempt < 50 && !intelligence.GetDiagnostics().Any(b => b.Path == "src/main.c" && b.Text == main); attempt++) { await Task.Delay(100); }
Check(intelligence.GetDiagnostics().Where(b => b.Path == "src/main.c").All(b => b.Text == main && b.Items.All(d => d.Severity != 1)), "fixed text clears earlier version diagnostics");
var cpp = "namespace app { struct Meter { int value; int get() { return value; } }; }\nstruct Other { int value; };\nint use(app::Meter& m, Other& other) { return m.value + other.value; }\n";
await File.WriteAllTextAsync(Path.Combine(fixture, "src/meter.cpp"), cpp);
var memberOffset = cpp.IndexOf("value", StringComparison.Ordinal);
var cppReferences = await intelligence.ReferencesAsync("src/meter.cpp", cpp, memberOffset, [new("src/meter.cpp", cpp)]);
Check(cppReferences.Count == 3, "C++ member references distinguish unrelated classes with same member name");
var cppRename = await intelligence.RenameAsync("src/meter.cpp", cpp, memberOffset, "value", "reading", [new(await files.ReadAsync(fixture, "src/meter.cpp"), cpp)]);
Check(cppRename.Count == 1 && cppRename[0].Matches.Count == 3 && cppRename[0].After.Contains("m.reading + other.value", StringComparison.Ordinal), "C++ semantic member rename preserves unrelated type member");
await intelligence.StopAsync();
Check(intelligence.GetDiagnostics().Count == 0, "project stop clears language diagnostics");
await LiveDiagnosticChecks.RunAsync(runtime, fixture, Path.Combine(root, "live-reliability"), Check);
await AnalysisLogChecks.RunAsync(runtime, Path.Combine(root, "analysis-logging"), Check);
await KeilCompilationChecks.RunAsync(runtime, fixture, Path.Combine(root, "keil-compilation"), Check);
await PackCompilationChecks.RunAsync(runtime, fixture, Path.Combine(root, "pack-compilation"), Check);
await InactiveCodeChecks.RunAsync(runtime, fixture, Path.Combine(root, "inactive-code"), Check);
await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), $"PASS {checks.Count}; no hardware or firmware build.\n" + string.Join('\n', checks));
Console.WriteLine($"PASS {checks.Count}; real clangd, isolated files and recovery stores; no hardware.");
