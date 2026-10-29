using StudioX.Application;
using StudioX.Application.Health;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args is ["--completion", var completionInstallation, var completionProject, var completionData])
{
    await CheckCompletionAsync(completionInstallation, completionProject, completionData);
    Console.WriteLine("PASS actual installed existing project compilation database and kernel completion");
    Console.WriteLine(typeof(BuildService).Assembly.Location);
    return;
}
if (args is ["--install-component", var archive, var installation, var installedUserData])
{
    var installedCatalog = new ToolsetCatalog(Path.Combine(installation, "runtime/toolsets"), installedUserData);
    var manager = new ToolManagementService(installedCatalog, new PackRepository(Path.Combine(installedUserData, "packs")), new RecentProjectService(installedUserData), installedUserData);
    var preview = await manager.PreviewInstallAsync(archive);
    Check(preview.Id == ZephyrDeviceCatalog.ToolsetId && preview.Version == ZephyrDeviceCatalog.ToolsetVersion, "Wrong component archive");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(preview, JsonStore.Options));
    Console.WriteLine(await manager.InstallAsync(preview));
    Console.WriteLine("PASS installed Zephyr component through managed archive transaction");
    return;
}
if (args is ["--installed-smoke", var installed, var packArchive, var newProject])
{
    var data = Path.Combine(Path.GetDirectoryName(newProject)!, "user-data");
    var smokeRepository = new PackRepository(Path.Combine(data, "packs"));
    var pack = await smokeRepository.ImportAsync(packArchive);
    var project = await new ZephyrProjectService().CreateFromPackAsync(pack, "STM32F407ZG", "zephyr_app", newProject);
    var tools = new ToolsetCatalog(Path.Combine(installed, "runtime/toolsets"));
    var report = await new BuildService(tools, userDataDirectory: data).BuildAsync(newProject);
    Check(report.Success, "Installed build failed: " + report.LogPath + "\n" + report.Log[^Math.Min(2000, report.Log.Length)..]);
    var commands = await File.ReadAllTextAsync(Path.Combine(newProject, ".build/compile_commands.json"));
    Check(!commands.Contains("/runs/", StringComparison.Ordinal) && !commands.Contains("\\\\runs\\\\", StringComparison.Ordinal), "Analysis database references removed snapshot");
    await CheckCompletionAsync(installed, newProject, data);
    Console.WriteLine("PASS actual installed project creation, locked SDK compilation, mapped database and Zephyr completion");
    Console.WriteLine(typeof(BuildService).Assembly.Location);
    return;
}

if (args.Length < 3)
{
    throw new ArgumentException("Usage: StudioX.ZephyrValidation <pack-dir> <toolsets-dir> <new-fixture-dir> [--all-builds]");
}
var packDirectory = Path.GetFullPath(args[0]);
var toolsets = new ToolsetCatalog(args[1]);
var fixture = Path.GetFullPath(args[2]);
if (Directory.Exists(fixture) && !args.Contains("--resume-builds"))
{
    throw new IOException("Fixture already exists; existing projects will not be overwritten.");
}
Directory.CreateDirectory(fixture);
var repository = new PackRepository(Path.Combine(fixture, "packs"));
var packs = new Dictionary<string, InstalledPack>();
var projects = new Dictionary<string, string>();
var service = new ZephyrProjectService();
foreach (var profile in ZephyrDeviceCatalog.Profiles)
{
    var directory = Path.Combine(fixture, "projects", "中文 路径", profile.DeviceId);
    if (args.Contains("--resume-builds"))
    {
        _ = await ProjectService.ReadAsync(directory);
        projects.Add(profile.DeviceId, directory);
        continue;
    }
    var line = profile.DeviceId[..9].ToLowerInvariant();
    if (!packs.TryGetValue(line, out var pack))
    {
        pack = await repository.ImportAsync(Path.Combine(packDirectory, "studiox." + line + "-0.1.3.mcupack"));
        packs.Add(line, pack);
    }
    var device = pack.Manifest.Devices.Single(d => d.Id == profile.DeviceId);
    Check(ZephyrTemplateCatalog.Templates(device).Count(t => ZephyrTemplateCatalog.IsZephyr(t)) == 1, "Missing or duplicate template: " + device.Id);
    Check(ZephyrDeviceCatalog.ForDevice(device with { FlashBytes = device.FlashBytes + 1024 }) is null, "Capacity mismatch offered a template");
    var created = await service.CreateFromPackAsync(pack, device.Id, "zephyr_app", directory);
    var opened = await ProjectService.ReadAsync(directory);
    Check(created == opened && opened.DeviceId == device.Id && !opened.Zephyr!.Experimental, "MCU identity was lost");
    Check((await ProjectDevelopmentComponents.ReadAsync(directory, opened)).Single() == ZephyrDeviceCatalog.Requirement, "Tool requirement was lost");
    Check(await service.FindBoardDevicetreeAsync(directory, opened) == opened.Zephyr!.Devicetree, "Board DTS navigation failed");
    projects.Add(profile.DeviceId, directory);
}
Console.WriteLine($"PASS create/open/template/identity for {projects.Count} devices");

var first = projects["STM32F407ZG"];
var original = await ProjectService.ReadAsync(first);
await JsonStore.WriteAsync(Path.Combine(first, ".studiox/project.json"), original with { Zephyr = original.Zephyr! with { BoardTarget = "black_f407zg_pro" } });
await RejectAsync(() => ProjectService.ReadAsync(first), "PROJECT_ZEPHYR_SETTINGS");
await JsonStore.WriteAsync(Path.Combine(first, ".studiox/project.json"), original);
var devicePath = Path.Combine(first, ".studiox/zephyr-device.json");
var savedDevice = await JsonStore.ReadAsync<DeviceDefinition>(devicePath);
await JsonStore.WriteAsync(devicePath, savedDevice with { RamBytes = savedDevice.RamBytes + 1024 });
await RejectAsync(() => ProjectService.ReadAsync(first), "PROJECT_ZEPHYR_DEVICE");
await JsonStore.WriteAsync(devicePath, savedDevice);
Console.WriteLine("PASS reject changed board / capacity");

var builds = new BuildService(toolsets, userDataDirectory: Path.Combine(fixture, "user-data"));
var health = await new ProjectHealthService(toolsets, builds).InspectAsync(first);
Check(health.CanBuild, "New Zephyr project health blocks compilation: " + health.Summary);
IEnumerable<ZephyrDeviceProfile> targets = args.Contains("--all-builds")
    ? ZephyrDeviceCatalog.Profiles.GroupBy(p => (p.SocConfig, p.Dtsi)).Select(g => g.First())
    : ZephyrDeviceCatalog.Profiles.Where(p => p.DeviceId is "STM32F407ZG" or "STM32F103C8");
var resolved = await toolsets.ResolveAsync(ZephyrDeviceCatalog.ToolsetId, ZephyrDeviceCatalog.ToolsetVersion, ZephyrDeviceCatalog.CompilerId);
var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 2 }, async (profile, token) =>
{
    Console.WriteLine("BUILD " + profile.DeviceId);
    var directory = projects[profile.DeviceId];
    if (args.Contains("--skip-completed") && File.Exists(Path.Combine(directory, ".build/zephyr/zephyr.elf")))
    {
        Console.WriteLine("PASS previous real compile " + profile.DeviceId);
        return;
    }
    var manifest = await ProjectService.ReadAsync(directory, token);
    var report = await new StudioX.Engine.Zephyr.ZephyrBuildService(Path.Combine(fixture, "user-data"))
        .ExecuteAsync(directory, manifest, resolved, false, null, token);
    if (!report.Success)
    {
        failures.Enqueue(profile.DeviceId + " / " + report.LogPath);
        Console.WriteLine("FAIL real compile " + profile.DeviceId + " / " + report.LogPath);
        return;
    }
    Check(report.Artifacts.Any(p => p.EndsWith(".elf", StringComparison.Ordinal)) && File.Exists(Path.Combine(directory, ".build/zephyr/zephyr.dts")), "Missing build outputs");
    Console.WriteLine("PASS real compile " + profile.DeviceId);
});
Check(failures.IsEmpty, "Build failures: " + string.Join("; ", failures));
var sourceFile = Path.Combine(first, "src/main.c");
var originalSource = await File.ReadAllTextAsync(sourceFile);
await File.AppendAllTextAsync(sourceFile, "\n#error expected_validation_failure\n");
BuildReport failure;
try
{
    failure = await builds.BuildAsync(first);
}
finally
{
    await File.WriteAllTextAsync(sourceFile, originalSource);
}
Check(!failure.Success && failure.Log.Contains("expected_validation_failure", StringComparison.Ordinal), "Raw compiler failure was lost");
Check(!File.Exists(Path.Combine(first, ".build/zephyr/zephyr.elf")), "Failed build left a stale ELF");
Console.WriteLine("PASS raw diagnostics and invalidated outputs after failed build");
Console.WriteLine("PASS Zephyr validation complete; no hardware accessed");

static void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task CheckCompletionAsync(string installation, string project, string data)
{
    await using var intelligence = new StudioX.Application.CodeIntelligence.CodeIntelligenceService(Path.Combine(installation, "runtime"), data);
    await intelligence.StartAsync(project);
    Check(intelligence.IsReady, "Installed C/C++ language service did not start");
    var text = (await File.ReadAllTextAsync(Path.Combine(project, "src/main.c")))
        .Replace("k_sleep(K_MSEC(1000));", "k_s", StringComparison.Ordinal);
    IReadOnlyList<StudioX.Application.CodeIntelligence.CodeSuggestion> suggestions = [];
    for (var attempt = 0; attempt < 10; attempt++)
    {
        suggestions = await intelligence.CompleteAsync("src/main.c", text, text.IndexOf("k_s", StringComparison.Ordinal) + 3);
        if (suggestions.Any(s => s.FilterText.Contains("k_sleep", StringComparison.Ordinal)))
        {
            break;
        }
        await Task.Delay(500);
    }
    Console.WriteLine("Completions: " + string.Join(" | ", suggestions.Take(30).Select(s => s.Label + " / " + s.FilterText)));
    await intelligence.FlushAnalysisLogAsync();
    Check(suggestions.Any(s => s.Label.Contains("k_sleep", StringComparison.Ordinal) || s.FilterText.Contains("k_sleep", StringComparison.Ordinal)),
        "Zephyr kernel completion missing; " + intelligence.AnalysisLogPath);
    Console.WriteLine("PASS actual installed Zephyr kernel completion");
}

static async Task RejectAsync(Func<Task<ProjectManifest>> action, string code)
{
    try
    {
        _ = await action();
    }
    catch (StudioXException error) when (error.Code == code)
    {
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + code);
}
