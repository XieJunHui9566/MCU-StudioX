using System.Security.Cryptography;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

if (args is ["--sdk-contract", var sdkContractRoot]) { await SdkContractChecks.RunAsync(sdkContractRoot); return; }
if (args is ["--sdk-cancel", var sdkCancelRoot]) { await SdkCancellationChecks.RunAsync(sdkCancelRoot); return; }

if (args is ["--qualified", var qualifiedRoot]) { await QualifiedChecks.RunAsync(qualifiedRoot); return; }
if (args is ["--qualified-lifecycle", var lifecycleRoot]) { await QualifiedChecks.RunAsync(lifecycleRoot, lifecycleOnly: true); return; }
if (args is ["--qualified-publication", var publicationRoot]) { await QualifiedPublicationChecks.RunAsync(publicationRoot); return; }
if (args is ["--qualified-legacy", var legacyRoot, var legacyTools]) { await LegacyBuildSmoke.RunAsync(legacyRoot, legacyTools); return; }
if (args is ["--current-inputs", var inputPack, var inputTools, var inputOutput, var inputMode]) { await CurrentInputChecks.RunAsync(inputPack, inputTools, inputOutput, inputMode == "observe"); return; }
if (args is ["--cmake", var cmakePack, var cmakeTools, var cmakeOutput, var userSource]) { await CMakeChecks.RunAsync(cmakePack, cmakeTools, cmakeOutput, userSource); return; }
if (args is ["--delay", var delayPack, var delayTools, var delayRuntime, var delayOutput]) { await DelayChecks.RunAsync(delayPack, delayTools, delayRuntime, delayOutput); return; }

if (args is ["--download", var downloadPack, var downloadTools, var downloadOutput]) { await DownloadChecks.RunAsync(downloadPack, downloadTools, downloadOutput); return; }
if (args is ["--download-hardware", var hardwarePack, var hardwareTools, var hardwareOutput]) { await DownloadHardwareChecks.RunAsync(hardwarePack, hardwareTools, hardwareOutput); return; }
if (args is ["--memory", var memoryPack, var memoryTools, var memoryRoot, var userProject]) { await MemoryChecks.RunAsync(memoryPack, memoryTools, memoryRoot, userProject); return; }
if (args is ["--settings", var packFile, var oldPack, var toolRoot, var settingsRoot]) { await SettingsChecks.RunAsync(packFile, oldPack, toolRoot, settingsRoot); return; }
if (args is ["--integration", var existingRoot]) { await IntegrationChecks.RunAsync(existingRoot); return; }
if (args is ["--package-smoke", var packageDirectory, var smokeRoot]) { await PackageSmokeChecks.RunAsync(packageDirectory, smokeRoot); return; }
if (args.Length != 3) { throw new ArgumentException("delivery-directory new-validation-directory round89-directory"); }
var delivery = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
var qualification = Path.GetFullPath(args[2]);
if (Directory.Exists(root)) { throw new IOException("Choose a new validation directory"); }
Directory.CreateDirectory(root);
var checks = new List<object>();
var projects = new ProjectService();
string Hash(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
void Require(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException(label);
    }
    checks.Add(new
    {
        label,
        passed = true
    });
    Console.WriteLine("PASS " + label);
}
async Task Error(Func<Task> operation, string code)
{
    try
    {
        await operation();
    }
    catch (StudioXException error) when (error.Code == code) { Require(true, code); return; }
    throw new InvalidOperationException("Expected diagnostic: " + code);
}
var package = Path.Combine(delivery, "stc.ai8051u-mx251-0.1.89.mcupack");
if (!File.Exists(package)) { await PackArchiveWriter.WriteAsync(Path.Combine(delivery, "pack-source"), package); }
var repository = new PackRepository(Path.Combine(root, "packs"));
var pack = await repository.ImportAsync(package);
Require(pack.Manifest.Devices.Single().Architecture == "mcs251", "format-1-device-pack-import");
var installation = Path.Combine(root, "temporary tools");
var catalog = new ToolsetCatalog(installation);
var environment = new ToolEnvironmentService(catalog);
var entry = new ToolEnvironmentEntry(Mx251Profile.ToolsetId, Mx251Profile.Version, "R89", Mx251Profile.CompilerId, 0, 0, false, "");
await environment.RepairAsync(entry, Path.Combine(delivery, "studiox.mx251-0.1.89-win-x64.mcutoolchain"), null, default, installOnly: true);
var moved = Path.Combine(root, "移动后 工具组件");
Directory.Move(installation, moved);
catalog = new ToolsetCatalog(moved);
var tools = await catalog.ResolveAsync(Mx251Profile.ToolsetId, Mx251Profile.Version, Mx251Profile.CompilerId, forceVerification: true);
Require(Hash(tools.Tool("sdcc")) == "6b257de531614340c295ab1c12e080cec6e170f24942afa949cbacadbc172733", "archive-import-relocated-compiler-unchanged");
var builder = new BuildService(catalog);
var results = new List<object>();
foreach (var model in new[] { "small", "large" })
{
    var destination = Path.Combine(root, "工程 空格", model);
    var project = await projects.CreateAsync(pack, "AI8051U-34K64", model, "test-" + model, destination);
    Require(!File.Exists(Path.Combine(destination, "CMakeLists.txt")), model + "-no-incorrect-cmake-scaffold");
    var cap = ProjectCapabilities.For(project);
    Require(cap.Build && !cap.Configure && cap.BuildSettings && !cap.SourceRegistration && cap.Download && !cap.Debug, model + "-explicit-capabilities");
    var report = await builder.BuildAsync(destination);
    await File.WriteAllTextAsync(Path.Combine(root, model + "-build.log"), report.Log);
    Require(report.Success && report.Artifacts.Any(p => p.EndsWith(".hex")), model + "-ide-build-service");
    var receipt = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(destination, ".build/studiox-build-receipt.json")))!;
    Require(receipt["images"]![0]!["format"]!.GetValue<string>() == "ihex", model + "-hex-receipt");
    results.Add(new
    {
        name = model,
        report.Artifacts,
        hash = Hash(report.Artifacts[0])
    });
}

var fixedCases = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(qualification, "fixed-projects.json")))!.AsArray();
foreach (var item in fixedCases)
{
    var name = item!["name"]!.GetValue<string>();
    var sample = Path.Combine(tools.RootDirectory, "examples/fixed", name);
    var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(sample, "project.json")))!;
    var destination = Path.Combine(root, "固定工程", name);
    await projects.CreateAsync(pack, "AI8051U-34K64", config["model"]!.GetValue<string>(), "fixed", destination);
    foreach (var file in Directory.EnumerateFiles(sample, "*", SearchOption.AllDirectories))
    {
        if (Path.GetFileName(file) == "project.json")
        {
            continue;
        }
        var target = Path.Combine(destination, Path.GetRelativePath(sample, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
    File.Copy(Path.Combine(sample, "project.json"), Path.Combine(destination, Mx251Profile.ConfigurationFile), overwrite: true);
    var report = await builder.BuildAsync(destination);
    await File.WriteAllTextAsync(Path.Combine(root, name + "-build.log"), report.Log);
    Require(report.Success, name + "-compiled");
    var expected = item["referenceSha256"]!.GetValue<string>();
    Require(Hash(report.Artifacts[0]) == expected, name + "-same-sealed-hex");
    results.Add(new
    {
        name,
        report.Artifacts,
        hash = Hash(report.Artifacts[0]),
        expected
    });
    await JsonStore.WriteAsync(Path.Combine(root, "build-results.json"), results);
}
var ordinary = Path.Combine(root, "工程 空格", "small");
var source = Path.Combine(ordinary, "src/main.c");
var original = await File.ReadAllTextAsync(source);
var configPath = Path.Combine(ordinary, Mx251Profile.ConfigurationFile);
var originalConfig = await File.ReadAllTextAsync(configPath);
var receiptPath = Path.Combine(ordinary, ".build/studiox-build-receipt.json");
await File.WriteAllTextAsync(source, original + "\n#error MX251_EXPECTED_TEST_FAILURE\n");
var failed = await builder.BuildAsync(ordinary);
Require(!failed.Success && failed.Log.Contains("MX251_EXPECTED_TEST_FAILURE") && !File.Exists(receiptPath), "compile-failure-original-diagnostic-and-no-stale-receipt");
await File.WriteAllTextAsync(source, original);
foreach (var invalid in new[] { "path", "runtime", "unknown" })
{
    var config = JsonNode.Parse(originalConfig)!;
    if (invalid == "path")
    {
        config["files"]!.AsArray().Add("../outside.c");
    }
    if (invalid == "runtime")
    {
        config["runtime"] = "native-math-wide";
    }
    if (invalid == "unknown")
    {
        config["rawFlags"] = "--unknown";
    }
    await File.WriteAllTextAsync(configPath, config.ToJsonString());
    await File.WriteAllTextAsync(receiptPath, "stale sentinel");
    failed = await builder.BuildAsync(ordinary);
    Require(!failed.Success && !File.Exists(receiptPath), invalid + "-rejected-no-stale-receipt");
}
await File.WriteAllTextAsync(configPath, originalConfig);
await Error(async () => { await builder.ConfigureAsync(ordinary); }, "MX251_CONFIGURE");

// 修改归档中的普通文件也必须触发工具内容锁，不能仅检查编译器EXE。
var notice = Path.Combine(tools.RootDirectory, "docs/使用说明.txt");
var priorNotice = await File.ReadAllBytesAsync(notice);
try
{
    await File.AppendAllTextAsync(notice, "\ntampered");
    await Error(async () => { await catalog.ResolveAsync(entry.Id, entry.Version, entry.CompilerId, forceVerification: true); }, "TOOL_HASH");
}
finally { await File.WriteAllBytesAsync(notice, priorNotice); }
await catalog.ResolveAsync(entry.Id, entry.Version, entry.CompilerId, forceVerification: true);
var recovered = await builder.BuildAsync(ordinary);
Require(recovered.Success && File.Exists(receiptPath), "recover-after-errors");

using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(700)))
{
    try
    {
        await builder.BuildAsync(Path.Combine(root, "固定工程", "full-math"), cancellationToken: cancel.Token);
        throw new InvalidOperationException("Cancellation did not interrupt build");
    }
    catch (OperationCanceledException) { Require(true, "cancellation-propagated"); }
}
Require(!File.Exists(Path.Combine(root, "固定工程/full-math/.build/studiox-build-receipt.json")), "cancellation-invalidates-receipt");
Require(Hash(tools.Tool("sdcc")) == "6b257de531614340c295ab1c12e080cec6e170f24942afa949cbacadbc172733", "compiler-unchanged-after-validation");
await JsonStore.WriteAsync(Path.Combine(root, "validation.json"), new { checks, builds = results, hardwareConnected = false, productVersionChanged = false });
Console.WriteLine("VALIDATION COMPLETE");
