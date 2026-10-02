using System.Diagnostics;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Editing;
using StudioX.Engine;
using StudioX.Foundation;

if (args.Length != 2) { throw new ArgumentException("fixture directory, NEW report directory required"); }
var fixture = Path.GetFullPath(args[0]);
var report = Path.GetFullPath(args[1]);
if (Directory.Exists(report)) { throw new IOException("Use a new report directory."); }
Directory.CreateDirectory(report);
if (!Directory.Exists(fixture))
{
    for (var module = 0; module < 300; module++)
    {
        var directory = Path.Combine(fixture, "sdk", $"module{module:000}");
        Directory.CreateDirectory(directory);
        for (var i = 0; i < 80; i++) { File.WriteAllText(Path.Combine(directory, $"header{i:000}.h"), "int sdk_symbol;\n"); }
    }
    Directory.CreateDirectory(Path.Combine(fixture, "src"));
    for (var i = 0; i < 6000; i++) { File.WriteAllText(Path.Combine(fixture, "src", $"source{i:0000}.c"), $"int function{i}(void) {{ return {i}; }}\n"); }
}
var discovery = new WorkspaceDiscoveryService(new ProjectFileService());
var measurements = new List<object>();
WorkspaceFileIndex? index = null;
foreach (var query in new[] { "", "s", "so", "sou", "source59", "module299/header079" })
{
    var allocated = GC.GetTotalAllocatedBytes(true);
    var watch = Stopwatch.StartNew();
    index ??= await discovery.CreateIndexAsync(fixture);
    var paths = await index.SearchAsync(query);
    measurements.Add(new { query, elapsedMs = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, results = paths.Count });
    Console.WriteLine($"{query}: {watch.Elapsed.TotalMilliseconds:F1} ms, {paths.Count} results");
}
await File.WriteAllTextAsync(Path.Combine(report, "quick-open.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true }));
var checks = new List<string>();
void Check(bool condition, string message)
{
    if (!condition) { throw new InvalidOperationException(message); }
    checks.Add(message);
    Console.WriteLine("PASS " + message);
}
Check(index!.Count == 30000, "30,000-file discovery is complete");
Check((await index.SearchAsync("SOURCE5999")).SequenceEqual(new[] { "src/source5999.c" }), "case-insensitive exact path search");
Check((await index.SearchAsync("source59")).Count == 100, "selective search preserves all matches");
Check((await index.SearchAsync("")).Count == 300, "empty search is bounded to 300 results");
var ranked = await index.SearchAsync("source");
Check(ranked[0] == "src/source0000.c" && ranked[^1] == "src/source0299.c", "filename prefix, length and ordinal ranking remain deterministic");
using (var stop = new CancellationTokenSource())
{
    stop.Cancel();
    try { await index.SearchAsync("source", stop.Token); throw new InvalidOperationException("Expected cancellation"); }
    catch (OperationCanceledException) { Check(true, "stale query cancellation is observed"); }
    Check((await index.SearchAsync("source5999")).Count == 1, "cancelled query does not cancel the shared snapshot");
    try { await discovery.CreateIndexAsync(fixture, stop.Token); throw new InvalidOperationException("Expected cancellation"); }
    catch (OperationCanceledException) { Check(true, "cancelled discovery cannot return a partial index"); }
}
using (var stop = new CancellationTokenSource())
using (var entries = new ProjectFileService().Enumerate(fixture, "src", stop.Token).GetEnumerator())
{
    Check(entries.MoveNext(), "wide directory starts streaming");
    stop.Cancel();
    try { entries.MoveNext(); throw new InvalidOperationException("Expected cancellation"); }
    catch (OperationCanceledException) { Check(true, "in-progress wide-directory enumeration cancels between entries"); }
}
var fresh = Path.Combine(fixture, "src", "new-external-file.c");
try
{
    File.WriteAllText(fresh, "int external_file;\n");
    Check((await index.SearchAsync("new-external")).Count == 0, "current picker snapshot remains immutable");
    Check((await (await discovery.CreateIndexAsync(fixture)).SearchAsync("new-external")).Count == 1, "new picker discovers external additions");
}
finally { File.Delete(fresh); }
Check((await new ProjectFileService().ListAsync(fixture, "src")).Count == 6000, "sorted asynchronous tree listing keeps all source entries");
foreach (var excluded in new[] { ".build", "node_modules", "cmake-build-debug" })
{
    var directory = Path.Combine(fixture, excluded);
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "excluded.c"), "");
}
Check((await discovery.CreateIndexAsync(fixture)).Count == 30000, "generated and dependency directories are excluded");
var unbuilt = Path.Combine(report, "unbuilt-sdk");
await JsonStore.WriteAsync(Path.Combine(unbuilt, ".studiox/project.json"), new ProjectManifest(1, "unbuilt-sdk", "espressif.esp32s3", "0.1.1", "",
    "ESP32-S3", "hello-world", "espressif.idf", "5.5.4", "esp-idf", Espressif: new("esp-idf", "esp32s3", "5.5.4")));
var unusedTools = Path.Combine(report, "absent-tools");
var memory = await new BuildMemoryService(new ToolsetCatalog(unusedTools)).ReadAsync(unbuilt);
Check(memory.Targets.Count == 0 && memory.Message.StartsWith("编译成功后", StringComparison.Ordinal) && !Directory.Exists(unusedTools),
    "unbuilt ESP-IDF project returns without SDK self-check or stale statistics");
await File.WriteAllTextAsync(Path.Combine(report, "checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
