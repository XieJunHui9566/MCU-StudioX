using System.Diagnostics;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args.Length != 2) throw new ArgumentException("Usage: StartupValidation <user-data-directory> <new-output-directory>");
var data = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new ArgumentException("Output must be a new directory.");
Directory.CreateDirectory(output);
var results = new List<string>();
void Pass(string text) { results.Add(text); Console.WriteLine(text); }

// 真实用户目录只读：分别测量记录读取与选择器目录，不访问历史工程或修改器件包。
var clock = Stopwatch.StartNew();
var recent = await new RecentProjectService(data).LoadAsync();
Pass($"recent: {recent.Count}, {clock.Elapsed.TotalMilliseconds:F1} ms");
clock.Restart();
var catalog = await new PackRepository(Path.Combine(data, "packs")).ListCatalogAsync();
Pass($"pack catalog: {catalog.Count}, {clock.Elapsed.TotalMilliseconds:F1} ms");

// 隔离的最小包用于证明延后校验不会把修改、丢失或新增的 SDK 文件复制到工程。
var source = Path.Combine(output, "source");
Directory.CreateDirectory(source);
await File.WriteAllTextAsync(Path.Combine(source, "main.c"), "int main(void) { for (;;) {} }\n");
await File.WriteAllTextAsync(Path.Combine(source, "support.c"), "int example;\n");
await File.WriteAllTextAsync(Path.Combine(source, "link.ld"), "MEMORY { FLASH(rx): ORIGIN=0x08000000, LENGTH=128K }\n");
var device = new DeviceDefinition("test-device", "Test device", "arm", 0x08000000, 131072, 0x20000000, 20480,
    "test.toolset", "1.0.0", "test-gcc", ["-mcpu=cortex-m3", "-mthumb"], [], [], ["support.c"], "link.ld", [], [],
    [new ProjectTemplate("bare", "Bare", "Test", "main.c")]);
var manifest = new PackManifest(1, "test.startup", "1.0.0", "Startup fixture", "Test", [device]);
await JsonStore.WriteAsync(Path.Combine(source, "manifest.json"), manifest);
var archive = Path.Combine(output, "test.mcupack");
await PackArchiveWriter.WriteAsync(source, archive);
var repository = new PackRepository(Path.Combine(output, "packs"));
await repository.ImportAsync(archive);
var selection = (await repository.ListCatalogAsync()).Single();
var projects = new ProjectService();
var validProject = Path.Combine(output, "valid-project");
await projects.CreateAsync(selection, device.Id, "bare", "valid_project", validProject);
if (!File.Exists(Path.Combine(validProject, "src", "main.c")) || !File.Exists(Path.Combine(validProject, "CMakeLists.txt")))
    throw new InvalidOperationException("Catalog selection did not generate a project.");
Pass("PASS: catalog selection creates project after full verification");
await ProjectDeviceInfoChecks.RunAsync(validProject, manifest, Pass);

async Task RejectCreate(string name)
{
    var destination = Path.Combine(output, name);
    try
    {
        await projects.CreateAsync(selection, device.Id, "bare", "test_project", destination);
        throw new InvalidOperationException("Damaged pack accepted: " + name);
    }
    catch (StudioXException ex) when (ex.Code == "PACK_HASH") { }
    if (Directory.Exists(destination) || Directory.EnumerateDirectories(output, ".studiox-create-*").Any())
        throw new InvalidOperationException("Failed verification left project files behind.");
    Pass("PASS: " + name + " rejected before copying");
}

var support = Path.Combine(selection.RootDirectory, "support.c");
// 保持文件大小和时间戳不变，确认实际使用仍检查内容哈希，而不是仅信任属性缓存。
var modified = File.GetLastWriteTimeUtc(support);
await File.WriteAllTextAsync(support, "int changed;\n");
File.SetLastWriteTimeUtc(support, modified);
_ = await repository.ListCatalogAsync();
await RejectCreate("changed-sdk");
await File.WriteAllTextAsync(support, "int example;\n");
File.Move(support, Path.Combine(output, "removed-support.c"));
await RejectCreate("missing-sdk");
File.Move(Path.Combine(output, "removed-support.c"), support);
var extra = Path.Combine(selection.RootDirectory, "extra.c");
await File.WriteAllTextAsync(extra, "int extra;\n");
await RejectCreate("unindexed-sdk");
File.Move(extra, Path.Combine(output, "extra.c"));

var manifestPath = Path.Combine(selection.RootDirectory, "manifest.json");
var originalManifest = await File.ReadAllBytesAsync(manifestPath);
await File.AppendAllTextAsync(manifestPath, " ");
try { await repository.ListCatalogAsync(); throw new InvalidOperationException("Changed manifest accepted."); }
catch (StudioXException ex) when (ex.Code == "PACK_HASH") { Pass("PASS: changed catalog manifest rejected"); }
await File.WriteAllBytesAsync(manifestPath, originalManifest);
try { await PackRepository.VerifyAsync(selection with { ContentHash = new string('0', 64) }); throw new InvalidOperationException("Stale selection accepted."); }
catch (StudioXException ex) when (ex.Code == "PACK_CHANGED") { Pass("PASS: stale selection rejected"); }
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await repository.ListCatalogAsync(cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); }
catch (OperationCanceledException) { Pass("PASS: catalog cancellation"); }
try { await PackRepository.VerifyAsync(selection, cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); }
catch (OperationCanceledException) { Pass("PASS: verification cancellation"); }

// 首次读取只访问记录文件；随后在后台清理确认已删除的本地工程。
var isolatedHistory = new RecentProjectService(Path.Combine(output, "user-data"));
var missingProject = Path.Combine(output, "missing-project");
var unknownNetworkProject = @"\\studiox-unavailable\share\missing-project";
await isolatedHistory.RememberAsync("valid", validProject);
await isolatedHistory.RememberAsync("missing", missingProject);
await isolatedHistory.RememberAsync("network", unknownNetworkProject);
if ((await isolatedHistory.LoadAsync()).Count != 3) throw new InvalidOperationException("Recent list probed projects during the initial read.");
Pass("PASS: initial recent read is independent of project availability");
var pruned = await isolatedHistory.PruneMissingLocalAsync();
if (pruned.Count != 2 || !pruned.Any(project => project.Name == "valid") || !pruned.Any(project => project.Name == "network") ||
    (await isolatedHistory.LoadAsync()).Any(project => project.Name == "missing"))
    throw new InvalidOperationException("Missing local project was not pruned and persisted, or valid/unknown project was removed.");
Pass("PASS: missing local project pruned while valid and unknown network projects remain");
Directory.Delete(validProject, recursive: true);
if (!await isolatedHistory.RemoveIfMissingLocalAsync(validProject) ||
    (await isolatedHistory.LoadAsync()).Any(project => project.Name == "valid"))
    throw new InvalidOperationException("Deleted recent project remained after click-time validation.");
if (!await isolatedHistory.RemoveIfMissingLocalAsync(validProject))
    throw new InvalidOperationException("A concurrently removed missing project was not recognized as missing.");
Pass("PASS: deleted local project removed on click-time validation");
if (!await isolatedHistory.RemoveAsync(unknownNetworkProject) || (await isolatedHistory.LoadAsync()).Count != 0)
    throw new InvalidOperationException("Explicit removal did not persist.");
Pass("PASS: explicit recent project removal persists");
await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
    isolatedHistory.RememberAsync("parallel-" + index, Path.Combine(output, "parallel-" + index))));
if ((await isolatedHistory.LoadAsync()).Count != 4)
    throw new InvalidOperationException("Concurrent recent writes lost an entry.");
Pass("PASS: concurrent recent writes preserve all entries");
await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), results);
