using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Health;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;
using StudioX.ProjectHealthValidation;

if (args.Length is not (2 or 4)) { throw new ArgumentException("Usage: health-validation <new-output> <ninja.exe> [real-project runtime]"); }
var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) { throw new InvalidOperationException("Use a new evidence directory."); }
Directory.CreateDirectory(output);
var ninja = await File.ReadAllBytesAsync(args[1]);
var checks = new List<string>();
void Check(bool value, string description)
{
    if (!value)
    {
        throw new InvalidOperationException(description);
    }
    checks.Add(description);
    Console.WriteLine("PASS " + description);
}
async Task ExpectCode(Func<Task> operation, string code)
{
    try
    {
        await operation();
        throw new InvalidOperationException("Expected " + code);
    }
    catch (StudioXException error) when (error.Code == code) { Check(true, "reject " + code); }
}
async Task<(string Root, ToolsetCatalog Catalog, ProjectHealthService Service, ProjectManifest Project)> Fixture(string name, bool esp = false)
{
    var parent = Path.Combine(output, name);
    var root = Path.Combine(parent, "中文 工程");
    var toolsRoot = Path.Combine(parent, "工具 runtime", "toolsets");
    var id = esp ? "espressif.idf" : "test.gcc";
    var version = esp ? "5.5.4" : "1.0.0";
    var compiler = esp ? "esp-idf" : "test-gcc";
    var toolsetRoot = Path.Combine(toolsRoot, id, version);
    Directory.CreateDirectory(toolsetRoot);
    await File.WriteAllBytesAsync(Path.Combine(toolsetRoot, "probe.exe"), ninja);
    var roles = new[] { "cmake", "ninja", "gcc", "gxx", "objcopy", "size" }.ToDictionary(role => role, _ => "probe.exe");
    var sha = new Dictionary<string, string> { ["probe.exe"] = Convert.ToHexString(SHA256.HashData(ninja)) };
    Dictionary<string, string>? resources = null;
    if (esp)
    {
        foreach (var role in new[] { "python", "git", "gcc-esp32s3", "gxx-esp32s3", "objcopy-esp32s3", "size-esp32s3" })
        {
            roles[role] = "probe.exe";
        }
        resources = new()
        {
            ["idf"] = "sdk",
            ["tools"] = "tools",
            ["python-env"] = "python"
        };
        foreach (var relative in new[] { "sdk/tools/idf.py", "sdk/tools/cmake/project.cmake", "tools/idf-env.json", "python/marker.txt" })
        {
            var path = Path.Combine(toolsetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "offline fixture\n");
            sha[relative] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        }
    }
    await JsonStore.WriteAsync(Path.Combine(toolsetRoot, "toolset.json"), new ToolsetManifest(1, id, version, "win-x64", compiler, roles, sha,
        ResourceDirectories: resources, Purpose: esp ? "esp-idf" : null));
    var project = new ProjectManifest(1, "test", "test.pack", "1.0.0", "offline", esp ? "ESP32-S3" : "TestDevice", "minimal", id, version, compiler,
        Espressif: esp ? new("esp-idf", "esp32s3", "5.5.4") : null);
    await JsonStore.WriteAsync(Path.Combine(root, ".studiox", "project.json"), project);
    var device = new DeviceDefinition(project.DeviceId, project.DeviceId, esp ? "xtensa" : "arm", 0, 65536, 0x20000000, 16384, id, version, compiler,
        [], [], [], [], "", [], [], [new("minimal", "Minimal", "Fixture", "main.c")], Espressif: esp ? new("esp-idf", "esp32s3", "5.5.4") : null);
    await JsonStore.WriteAsync(Path.Combine(root, "device", "manifest.json"), new PackManifest(1, "test.pack", "1.0.0", "Offline", "Test", [device]));
    foreach (var relative in new[] { "CMakeLists.txt", "device/CMakeLists.txt", "device/platform.cmake", "src/main.c", "sdkconfig" })
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, relative == "sdkconfig" ? "CONFIG_IDF_TARGET=\"esp32s3\"\n" : "// offline fixture\n");
    }
    var catalog = new ToolsetCatalog(toolsRoot);
    return (root, catalog, new ProjectHealthService(catalog, new BuildService(catalog)), project);
}
async Task<Dictionary<string, string>> Snapshot(string root)
{
    var result = new Dictionary<string, string>();
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        result[Path.GetRelativePath(root, file).Replace('\\', '/')] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file)));
    }
    return result;
}
var good = await Fixture("normal");
var before = await Snapshot(Path.GetDirectoryName(good.Root)!);
var report = await good.Service.InspectAsync(good.Root);
Check(report.CanBuild && report.Errors == 0, "spaces and Chinese paths are healthy");
Check(report.Checks.Any(check => check.Code == "HEALTH_TOOL_ENTRIES" && check.State == HealthState.Information), "fast check explicitly leaves tool content unverified");
Check(!File.Exists(Path.Combine(good.Root, ".studiox", "toolchain.lock.json")), "inspection does not create a tool lock");
var after = await Snapshot(Path.GetDirectoryName(good.Root)!);
Check(before.Count == after.Count && before.All(entry => after[entry.Key] == entry.Value), "quick inspection leaves every project and tool input unchanged");
var deep = await good.Service.InspectAsync(good.Root, deep: true);
Check(deep.CanBuild && deep.Checks.Count(check => check.Code == "HEALTH_TOOL_START") == 3, "deep verification runs bounded version checks against verified executables");
Check(deep.Checks.Any(check => check.Code == "HEALTH_TOOL_HASH"), "deep hash result is distinct from configuration checks");
Check(deep.Checks.Where(check => check.Code == "HEALTH_TOOL_START").All(check => check.RawDiagnostic.Trim().Length > 0), "version command output is preserved");
var noProject = await good.Service.InspectAsync(null);
Check(!noProject.CanBuild && noProject.Errors == 0 && noProject.Checks.Single().Code == "HEALTH_NO_PROJECT", "no-project state does not invent a target");
var missingProject = await good.Service.InspectAsync(Path.Combine(output, "not-present"));
Check(!missingProject.CanBuild && missingProject.Errors == 1 && missingProject.Checks.Single().RawDiagnostic.Contains("NotFoundException"), "unopened missing projects retain the original exception");
var missing = await Fixture("missing-tool");
await JsonStore.WriteAsync(Path.Combine(missing.Root, ".studiox", "project.json"), missing.Project with { ToolsetVersion = "2.0.0" });
var missingReport = await missing.Service.InspectAsync(missing.Root);
Check(!missingReport.CanBuild && missingReport.Checks.Any(check => check.Code == "TOOLSET_MISSING" && check.ToolsetVersion == "2.0.0" && check.Action == HealthAction.Tools), "missing locked version directs repair to that exact toolset");
var corrupt = await Fixture("corrupt-tool");
await File.AppendAllTextAsync(Path.Combine(corrupt.Catalog.RootDirectory, "test.gcc", "1.0.0", "probe.exe"), "changed");
Check((await corrupt.Service.InspectAsync(corrupt.Root)).CanBuild, "fast presence check does not pretend to detect content corruption");
var corruptReport = await corrupt.Service.InspectAsync(corrupt.Root, deep: true);
Check(!corruptReport.CanBuild && corruptReport.Checks.Any(check => check.Code == "TOOL_HASH"), "deep check detects corrupted executable content");
var locked = await Fixture("locked-content");
await JsonStore.WriteAsync(Path.Combine(locked.Root, ".studiox", "toolchain.lock.json"), new ToolchainLock(1, "test.gcc", "1.0.0", "wrong"));
var lockBefore = await File.ReadAllBytesAsync(Path.Combine(locked.Root, ".studiox", "toolchain.lock.json"));
Check((await locked.Service.InspectAsync(locked.Root)).Checks.Any(check => check.Code == "TOOLCHAIN_LOCK" && check.State == HealthState.Error), "changed content fingerprint blocks a build");
var lockAfter = await File.ReadAllBytesAsync(Path.Combine(locked.Root, ".studiox", "toolchain.lock.json"));
Check(lockBefore.SequenceEqual(lockAfter), "tool lock mismatch never deletes or rewrites the lock");
var badSettings = await Fixture("invalid-settings");
await File.WriteAllTextAsync(Path.Combine(badSettings.Root, ".studiox", "build.json"), "{\"formatVersion\":99}");
Check((await badSettings.Service.InspectAsync(badSettings.Root)).Checks.Any(check => check.Code == "BUILD_SETTINGS" && check.Action == HealthAction.BuildSettings), "invalid build parameters have a direct settings action");
var sdk = await Fixture("esp-sdk", true);
var analysisFixture = await Fixture("analysis-environment", true);
await AnalysisEnvironmentChecks.RunAsync(analysisFixture.Root, analysisFixture.Project, analysisFixture.Catalog, analysisFixture.Service, Check);
Check((await sdk.Service.InspectAsync(sdk.Root)).CanBuild, "ESP-IDF SDK identity and locked target are checked without invoking idf.py");
var wrongTargetTool = await Fixture("missing-target-compiler", true);
var wrongTargetManifestPath = Path.Combine(wrongTargetTool.Catalog.RootDirectory, "espressif.idf", "5.5.4", "toolset.json");
var wrongTargetManifest = await JsonStore.ReadAsync<ToolsetManifest>(wrongTargetManifestPath);
wrongTargetManifest.Executables.Remove("gcc-esp32s3");
await JsonStore.WriteAsync(wrongTargetManifestPath, wrongTargetManifest);
var wrongTargetReport = await wrongTargetTool.Service.InspectAsync(wrongTargetTool.Root);
Check(!wrongTargetReport.CanBuild && wrongTargetReport.Checks.Any(check => check.Code == "TOOL_ROLE"), "missing target compiler cannot fall back to the generic compiler");
await File.WriteAllTextAsync(Path.Combine(sdk.Root, "sdkconfig"), "CONFIG_IDF_TARGET=\"esp32\"\n");
Check((await sdk.Service.InspectAsync(sdk.Root)).Checks.Any(check => check.Code == "HEALTH_SDK_CONFIG_TARGET" && check.State == HealthState.Warning), "root sdkconfig target conflict requests explicit review");
var absentSdk = await Fixture("absent-sdk", true);
File.Move(Path.Combine(absentSdk.Catalog.RootDirectory, "espressif.idf", "5.5.4", "sdk", "tools", "idf.py"), Path.Combine(output, "preserved-idf.py"));
Check((await absentSdk.Service.InspectAsync(absentSdk.Root)).Checks.Any(check => check.Code == "TOOL_RESOURCE" && check.State == HealthState.Error), "missing native SDK entry prevents build");
var cache = await Fixture("cache-repair");
Directory.CreateDirectory(Path.Combine(cache.Root, ".build", "CMakeFiles"));
await File.WriteAllTextAsync(Path.Combine(cache.Root, ".build", "CMakeCache.txt"), "CMAKE_HOME_DIRECTORY:INTERNAL=" + output + "\n");
await File.WriteAllTextAsync(Path.Combine(cache.Root, ".build", "CMakeFiles", "compiler.obj"), "generated object");
await File.WriteAllTextAsync(Path.Combine(cache.Root, ".build", "studiox-build-receipt.json"), "old receipt");
await File.WriteAllTextAsync(Path.Combine(cache.Root, ".build", "firmware.bin"), "retained firmware");
await File.WriteAllTextAsync(Path.Combine(cache.Root, ".build", "sdkconfig"), "retained SDK config");
await File.WriteAllTextAsync(Path.Combine(cache.Root, ".build", "studiox-build.log"), "raw tool diagnostics");
var cacheReport = await cache.Service.InspectAsync(cache.Root);
Check(!cacheReport.CanBuild && cacheReport.Checks.Any(check => check.Code == "HEALTH_CACHE_PROJECT" && check.Action == HealthAction.ResetCache), "copied CMake cache blocks configuration and provides repair action");
var cacheBefore = await Snapshot(cache.Root);
var plan = await cache.Service.PreviewCacheRepairAsync(cache.Root);
Check(plan.Entries.Count == 3 && plan.Entries.All(entry => entry.RelativePath.StartsWith(".build/")), "repair preview includes only known generated cache and receipt");
Check(cacheBefore.All(entry => File.Exists(Path.Combine(cache.Root, entry.Key))), "preview does not move any inputs");
var forged = plan with { Entries = plan.Entries.Append(new("sdkconfig", false, 1, 1, "forged")).ToArray() };
await ExpectCode(() => cache.Service.RepairCacheAsync(forged), "HEALTH_CACHE_CHANGED");
var withDebug = new ProjectHealthService(cache.Catalog, new BuildService(cache.Catalog), () => true);
await ExpectCode(() => withDebug.RepairCacheAsync(plan), "HEALTH_DEBUG_ACTIVE");
var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await cache.Service.RepairCacheAsync(plan, cancelled.Token);
    throw new InvalidOperationException("Expected cancellation");
}
catch (OperationCanceledException) { Check(File.Exists(Path.Combine(cache.Root, ".build", "CMakeCache.txt")), "cancelled repair preserves cache"); }
var backup = await cache.Service.RepairCacheAsync(plan);
Check(Directory.Exists(backup) && File.Exists(Path.Combine(backup, "CMakeCache.txt")) && File.Exists(Path.Combine(backup, "CMakeFiles", "compiler.obj")), "repair moves original generated content to a local backup");
Check(!File.Exists(Path.Combine(cache.Root, ".build", "studiox-build-receipt.json")) && File.Exists(Path.Combine(backup, "studiox-build-receipt.json")), "repair invalidates the old download receipt while retaining it in backup");
foreach (var relative in new[] { "CMakeLists.txt", "src/main.c", "sdkconfig", ".studiox/project.json", ".build/sdkconfig", ".build/firmware.bin", ".build/studiox-build.log" })
{
    Check(cacheBefore[relative] == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(cache.Root, relative)))), "repair preserves " + relative);
}
Check((await cache.Service.InspectAsync(cache.Root)).CanBuild, "repaired cache leaves a clean first-configuration state");
var stale = await Fixture("stale-preview");
Directory.CreateDirectory(Path.Combine(stale.Root, ".build"));
await File.WriteAllTextAsync(Path.Combine(stale.Root, ".build", "CMakeCache.txt"), "first");
var stalePlan = await stale.Service.PreviewCacheRepairAsync(stale.Root);
await File.AppendAllTextAsync(Path.Combine(stale.Root, ".build", "CMakeCache.txt"), "changed");
await ExpectCode(() => stale.Service.RepairCacheAsync(stalePlan), "HEALTH_CACHE_CHANGED");
Check(File.Exists(Path.Combine(stale.Root, ".build", "CMakeCache.txt")), "stale preview rejection retains new cache content");
var rollback = await Fixture("partial-rollback");
Directory.CreateDirectory(Path.Combine(rollback.Root, ".build"));
var rollbackFirst = Path.Combine(rollback.Root, ".build", "CMakeCache.txt");
var rollbackSecond = Path.Combine(rollback.Root, ".build", "compile_commands.json");
await File.WriteAllTextAsync(rollbackFirst, "original cache");
await File.WriteAllTextAsync(rollbackSecond, "original database");
var rollbackPlan = await rollback.Service.PreviewCacheRepairAsync(rollback.Root);
using (var occupied = new FileStream(rollbackSecond, FileMode.Open, FileAccess.Read, FileShare.None))
{
    try
    {
        await rollback.Service.RepairCacheAsync(rollbackPlan);
        throw new InvalidOperationException("Expected occupied cache failure");
    }
    catch (IOException) { Check(await File.ReadAllTextAsync(rollbackFirst) == "original cache", "partial repair failure restores the first moved cache file"); }
}
Check(await File.ReadAllTextAsync(rollbackSecond) == "original database", "occupied cache file retains original content after rollback");
Check(File.Exists(Path.Combine(rollback.Root, "sdkconfig")), "partial rollback leaves project SDK configuration in place");
Check(TroubleshootingService.Explain("TOOLCHAIN_LOCK").Action == "tools", "tool lock diagnostics route to tools");
Check(TroubleshootingService.Explain("HEALTH_CACHE_PROJECT CMAKE_HOME_DIRECTORY").Action == "health", "cache diagnostics route directly to health repair");
await good.Service.ExportAsync(report, Path.Combine(output, "export.json"));
Check((await JsonStore.ReadAsync<ProjectHealthReport>(Path.Combine(output, "export.json"))).Checks.Count == report.Checks.Count, "export retains all original structured checks");
if (args.Length == 4)
{
    var realRoot = Path.GetFullPath(args[2]);
    var realCatalog = new ToolsetCatalog(Path.Combine(args[3], "toolsets"));
    var real = new ProjectHealthService(realCatalog, new BuildService(realCatalog));
    var selected = new[] { ".studiox/project.json", ".studiox/toolchain.lock.json", "CMakeLists.txt", "sdkconfig", "device/manifest.json" }
        .Where(relative => File.Exists(Path.Combine(realRoot, relative))).ToDictionary(relative => relative, relative => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(realRoot, relative)))));
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var realReport = await real.InspectAsync(realRoot);
    timer.Stop();
    await real.ExportAsync(realReport, Path.Combine(output, "real-project-report.json"));
    Check(realReport.CanBuild, "real ESP32-S3 project passes fast preflight");
    Check(selected.All(entry => entry.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(realRoot, entry.Key))))), "real project configuration and tool lock remain unchanged");
    await File.WriteAllTextAsync(Path.Combine(output, "real-check-timing.json"), JsonSerializer.Serialize(new
    {
        milliseconds = timer.ElapsedMilliseconds,
        realReport.Summary
    }));
}
await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Passed {checks.Count} checks.");
