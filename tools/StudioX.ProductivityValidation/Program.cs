using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Editing;
using StudioX.Application.Plugins;
using StudioX.Engine;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

if (args.Length != 4) { throw new ArgumentException("runtime, fixture, sample DLL, NEW output directory required"); }
var runtime = Path.GetFullPath(args[0]);
var fixture = Path.GetFullPath(args[1]);
var sampleDll = Path.GetFullPath(args[2]);
var root = Path.GetFullPath(args[3]);
if (Directory.Exists(root)) { throw new IOException("Use a new output directory."); }
Directory.CreateDirectory(root);
var checks = new List<string>();
void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } checks.Add(message); Console.WriteLine("PASS " + message); }
async Task Reject(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (Exception ex) when (ex is StudioXException or IOException or ArgumentException or OperationCanceledException) { Check(true, message); return; }
    throw new InvalidOperationException("Expected rejection: " + message);
}
var project = Path.Combine(root, "project");
foreach (var relative in new[] { ".studiox/project.json", "device/manifest.json", "src/main.c", "src/other.c", "include/shared.h", "src/meter.cpp" })
{
    var output = Path.Combine(project, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.Copy(Path.Combine(fixture, relative), output);
}
await File.WriteAllTextAsync(Path.Combine(project, "demo.sxdemo"), "pri");
var data = Path.Combine(root, "data");
var history = new LocalHistoryService(data);
var files = new ProjectFileService(history);
var source = await files.ReadAsync(project, "src/main.c");
var original = source.Text;
source = await files.SaveAsync(project, source, original + "// saved\n");
var versions = await history.ListAsync(project, "src/main.c");
Check(versions.Count == 1 && versions[0].Text == original, "save captures recoverable baseline with hash");
await history.CaptureAsync(project, "src/main.c", original, "duplicate");
Check((await history.ListAsync(project, "SRC/MAIN.C")).Count == 1, "history deduplicates and Windows path casing is stable");
var plan = LocalHistoryService.RestorePlan(new(source, source.Text), versions[0]);
Check(plan.After == original && (await files.ReadAsync(project, "src/main.c")).Text == source.Text, "history restoration previews without writing disk");
await Reject(() => Task.FromResult(LocalHistoryService.RestorePlan(new(source, source.Text), versions[0] with { Hash = "bad" })), "corrupt history cannot restore");
Check((await history.ListAsync(Path.Combine(root, "other-project"), "src/main.c")).Count == 0, "history is isolated by project");
for (var i = 0; i < 54; i++) { await history.CaptureAsync(project, "src/main.c", "version " + i, "retention"); }
Check((await history.ListAsync(project, "src/main.c")).Count == 50, "history retention bounds each file to 50 versions");
var layout = new WorkbenchLayoutService(data);
await layout.SaveAsync(new(Width: -1, Height: 50000, SplitRatio: 3));
var loaded = await layout.LoadAsync();
Check(loaded.Width == 1000 && loaded.Height == 3000 && loaded.SplitRatio == .8, "saved layout clamps invalid sizes and split ratios");
await using (var intelligence = new CodeIntelligenceService(runtime, data))
{
    await intelligence.StartAsync(project);
    var compact = "#include \"shared.h\"\nint shared_value=1;\nint main(void){if(shared_value){return helper();}return 0;}\n";
    var changes = await intelligence.FormatAsync(new(source, compact), null, 0, [new("src/main.c", compact)]);
    Check(changes.Count == 1 && changes[0].After.Contains("if (shared_value)"), "real clangd formats unsaved file");
    await new WorkspaceEditService(files).ValidateAsync(project, changes, [new(source, compact)]);
    Check(true, "format uses validated workspace edit transaction");
    await Reject(() => new WorkspaceEditService(files).ValidateAsync(project, changes, [new(source, compact + "// raced")]), "format preview rejects edits made after request");
    var ranged = await intelligence.FormatAsync(new(source, compact), compact.IndexOf("int main", StringComparison.Ordinal), compact.Length - compact.IndexOf("int main", StringComparison.Ordinal), [new("src/main.c", compact)]);
    await JsonStore.WriteAsync(Path.Combine(root, "range-edits.json"), ranged.Select(c => new { c.Before, c.After, c.Matches }));
    Check(ranged.Count == 1 && ranged[0].After.StartsWith("#include \"shared.h\"\n", StringComparison.Ordinal) && ranged[0].After.Contains("if (shared_value)"), "range formatting previews formatter edits including any adjacent context");
    var broken = "int answer(void) { return 42 }\n";
    await intelligence.SynchronizeDiagnosticsAsync("src/main.c", broken, [new("src/main.c", broken)]);
    for (var attempt = 0; attempt < 60 && !intelligence.GetDiagnostics().Any(b => b.Text == broken && b.Items.Count > 0); attempt++)
    {
        await Task.Delay(100);
    }
    var fixes = await intelligence.QuickFixesAsync(new(source, broken), broken.IndexOf('}'), [new("src/main.c", broken)]);
    Check(fixes.Any(f => f.Changes.Any(c => c.After.Contains("42;"))), "real clangd quick fix repairs missing semicolon");
    var symbols = await intelligence.SearchSymbolsAsync("answer");
    Check(symbols.Any(s => s.DocumentPath == "src/main.c"), "workspace symbol search includes unsaved document symbols");
}
var toolRoot = Path.Combine(root, "tools");
var installed = Path.Combine(toolRoot, "fixture.tools", "1.0.0");
Directory.CreateDirectory(installed);
var toolBytes = Encoding.UTF8.GetBytes("isolated tool payload; never executed");
await File.WriteAllBytesAsync(Path.Combine(installed, "tool.exe"), toolBytes);
var toolManifest = new ToolsetManifest(1, "fixture.tools", "1.0.0", "win-x64", "fixture", new() { ["mapper"] = "tool.exe" }, new() { ["tool.exe"] = Convert.ToHexString(SHA256.HashData(toolBytes)) }, Purpose: "hdl-native");
await JsonStore.WriteAsync(Path.Combine(installed, "toolset.json"), toolManifest);
var catalog = new ToolsetCatalog(toolRoot);
var environment = new ToolEnvironmentService(catalog);
var row = (await environment.InspectAsync(null)).Single();
Check(row.Files == 1 && row.Bytes == toolBytes.Length, "environment reports actual file count and size");
var archive = Path.Combine(root, "fixture.studioxtools");
await environment.ExportAsync(row, archive, null);
using (var content = File.OpenRead(archive))
using (var container = ToolchainArchive.Open(content))
    Check(container.Container == "7z", "offline tool export uses 7z even with legacy archive extension");
await File.WriteAllTextAsync(Path.Combine(installed, "tool.exe"), "damaged");
await Reject(() => environment.VerifyAsync(row, null), "tool verification detects damaged installed payload");
var backup = await environment.RepairAsync(row, archive, null);
await environment.VerifyAsync(row, null);
Check(await File.ReadAllTextAsync(Path.Combine(backup, "tool.exe")) == "damaged" && catalog.ManifestPaths().Count() == 1, "offline repair restores verified files and excludes preserved rollback from catalog");
await Reject(() => environment.RepairAsync(row with { Version = "9.0.0" }, archive, null), "offline repair refuses wrong locked version");
var corrupt = Path.Combine(root, "corrupt.studioxtools");
await LegacyZipFixture(archive, corrupt);
using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update)) { zip.GetEntry("tool.exe")!.Delete(); await using var output = zip.CreateEntry("tool.exe").Open(); await output.WriteAsync(Encoding.UTF8.GetBytes("bad")); }
await Reject(() => environment.RepairAsync(row, corrupt, null), "corrupt offline archive rejected before installed tools are replaced");
await environment.VerifyAsync(row, null);
Check(true, "failed repair preserves usable installed version");
var linked = Path.Combine(root, "linked.studioxtools");
await LegacyZipFixture(archive, linked);
using (var zip = ZipFile.Open(linked, ZipArchiveMode.Update)) { zip.GetEntry("tool.exe")!.ExternalAttributes = unchecked((int)0xA1FF0000); }
await Reject(() => environment.RepairAsync(row, linked, null), "offline repair rejects symbolic link entries before replacement");
using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update)) { await using var output = zip.CreateEntry("../escape").Open(); await output.WriteAsync(new byte[] { 1 }); }
await Reject(() => environment.RepairAsync(row, corrupt, null), "unindexed archive traversal rejected");
Check(TroubleshootingService.Explain("TOOLSET_MISSING").Action == "tools", "missing tool diagnostic offers environment action");
Check(TroubleshootingService.Explain("fatal error: shared.h: file not found").Action == "problems", "missing include diagnostic offers source navigation");
var pluginRoot = Path.Combine(data, "plugins", "studiox.development");
Directory.CreateDirectory(pluginRoot);
File.Copy(sampleDll, Path.Combine(pluginRoot, Path.GetFileName(sampleDll)));
var pluginManifest = new PluginManifest(1, 3, "studiox.development", "1.0.0", "开发扩展示例", Path.GetFileName(sampleDll), "StudioX.SamplePlugin.DevelopmentToolsPlugin", ["commands", "settings", "events", "languages", "debugAdapters"], new() { [Path.GetFileName(sampleDll)] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sampleDll))) });
await JsonStore.WriteAsync(Path.Combine(pluginRoot, "plugin.json"), pluginManifest);
await using (var manager = new PluginManagerService(runtime, data))
{
    await manager.SetEnabledAsync(pluginManifest.Id, true);
    await using var workspace = await manager.OpenWorkspaceAsync(project, (_, _, _, _) => throw new InvalidOperationException("Sample must not call hardware"));
    Check(workspace.Contributions.Any(p => p.Id == pluginManifest.Id) && workspace.IsPluginRunning(pluginManifest.Id), "API 3 example runs in isolated plugin host");
    var contribution = workspace.Contributions.Single(p => p.Id == pluginManifest.Id).Contribution;
    await Reject(() => Task.Run(() => PluginContributionValidator.Validate(pluginManifest with { ApiVersion = 2 }, contribution)), "API 2 cannot smuggle API 3 contributions");
    var settings = new PluginSettingsService(data);
    var values = new Dictionary<string, JsonElement>(await settings.ReadAsync(pluginManifest.Id, contribution.Settings)) { ["greeting"] = JsonSerializer.SerializeToElement("Test greeting") };
    await settings.SaveAsync(pluginManifest.Id, contribution.Settings, values);
    Check((await settings.ReadAsync(pluginManifest.Id, contribution.Settings))["greeting"].GetString() == "Test greeting", "typed plugin settings persist independently of installation");
    await Reject(() => settings.SaveAsync(pluginManifest.Id, contribution.Settings, new Dictionary<string, JsonElement>(values) { ["showDetails"] = JsonSerializer.SerializeToElement("wrong") }), "plugin settings reject mismatched types");
    await workspace.PublishWorkspaceEventAsync("settings.changed", values);
    await workspace.PublishWorkspaceEventAsync("document.opened", new
    {
        path = "demo.sxdemo"
    });
    var status = await workspace.InvokeAsync(pluginManifest.Id, "command", "status", JsonSerializer.SerializeToElement(new
    {
    }));
    Check(status.GetProperty("events").GetInt32() == 2 && status.GetProperty("greeting").GetString() == "Test greeting", "settings and document events reach real plugin process");
    var completions = await workspace.CompleteAsync(new("completion", "demo.sxdemo", "pri", 3, 1));
    Check(completions.Any(c => c.Label == "print" && c.Detail == "Test greeting"), "custom language completion returns from plugin process");
    await workspace.PublishWorkspaceEventAsync("settings.changed", new { showDetails = false });
    Check((await workspace.CompleteAsync(new("completion", "demo.sxdemo", "pri", 3, 2))).All(c => c.Detail == ""), "boolean setting changes the example's completion presentation");
    var panel = await workspace.AdaptDebugSnapshotAsync(pluginManifest.Id, "snapshot", JsonSerializer.SerializeToElement(new
    {
        state = "Disconnected",
        hardware = false
    }));
    Check(panel.Widgets.Any(widget => widget.Id == "state" && widget.Value?.GetString()?.Contains("Disconnected") == true)
        && panel.Widgets.Any(widget => widget.Id == "mode" && widget.Value?.GetString()?.Contains("未连接芯片") == true),
        "debug snapshot adapter reports disconnected state and offline source without starting hardware");
    await Reject(() => workspace.InvokeAsync(pluginManifest.Id, "language", "unknown", JsonSerializer.SerializeToElement(new { })), "undeclared plugin adapter invocation rejected");
}
var legacyRoot = Path.Combine(root, "api2-plugin");
Directory.CreateDirectory(legacyRoot);
File.Copy(sampleDll, Path.Combine(legacyRoot, Path.GetFileName(sampleDll)));
await JsonStore.WriteAsync(Path.Combine(legacyRoot, "plugin.json"), pluginManifest with { ApiVersion = 2, Id = "studiox.workspace-overview", EntryType = "StudioX.SamplePlugin.WorkspaceOverviewPlugin", Capabilities = ["commands", "panels", "agentTools"], HostTools = ["project_info", "serial_status", "serial_read"] });
await using (var legacy = await PluginRuntimeClient.StartAsync(Path.Combine(runtime, "plugin-host", "StudioX.PluginHost.exe"), Path.Combine(legacyRoot, "plugin.json"), (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { })), (_, _) => Task.CompletedTask))
{
    var result = await legacy.InvokeAsync("command", "refresh", JsonSerializer.SerializeToElement(new
    {
    }));
    Check(result.TryGetProperty("project", out _), "existing API 2 command and panel plugin still activates and responds");
}
var discovery = new WorkspaceDiscoveryService(files);
var large = Path.Combine(root, "large-sdk");
Directory.CreateDirectory(large);
for (var i = 0; i < 12000; i++) { var dir = Path.Combine(large, "module" + i / 100); Directory.CreateDirectory(dir); await File.WriteAllTextAsync(Path.Combine(dir, "header" + i + ".h"), "int sdk_symbol;\n"); }
var watch = Stopwatch.StartNew();
var found = await discovery.FilesAsync(large, "header11999");
watch.Stop();
Check(found.Count == 1, "quick open locates file in 12000-file SDK fixture");
var quickOpenMs = watch.ElapsedMilliseconds;
using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); await Reject(() => discovery.FilesAsync(large, "header", cancelled.Token), "large tree quick open cancels stale requests"); }
watch.Restart();
await Reject(() => new WorkspaceEditService(files).SearchAsync(large, new("sdk_symbol", true, true, false), null, "*.h", "", true, []), "large SDK search rejects oversized result with explicit limit diagnostic");
watch.Stop();
await File.WriteAllTextAsync(Path.Combine(root, "performance.txt"), $"12000 small headers; quick open {quickOpenMs} ms; bounded project search {watch.ElapsedMilliseconds} ms; process working set {Process.GetCurrentProcess().WorkingSet64 / 1048576} MiB. Synthetic offline fixture, not hardware.\n");
await File.WriteAllTextAsync(Path.Combine(root, "result.txt"), "PASS " + checks.Count + "\n" + string.Join('\n', checks));
Console.WriteLine("PASS " + checks.Count);

static async Task LegacyZipFixture(string source, string destination)
{
    using var file = File.OpenRead(source);
    using var container = ToolchainArchive.Open(file);
    using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
    await container.ReadFilesAsync(async (entry, content) =>
    {
        using var output = zip.CreateEntry(entry.Name).Open();
        await ToolchainArchive.CopyExactAsync(content, output, entry.Length);
    });
}
