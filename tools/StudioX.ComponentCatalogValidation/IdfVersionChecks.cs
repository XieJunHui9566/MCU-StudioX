namespace StudioX.ComponentCatalogValidation;

using System.Security.Cryptography;
using System.Text;
using StudioX.Application;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class IdfVersionChecks
{
    private static readonly (string Sdk, string Pack)[] Versions = [("5.5.5", "0.2.0"), ("6.0.3", "0.3.0"), ("6.1.0", "0.4.0")];
    private static readonly (string Target, string Device, string Template)[] Cases =
        [("esp32s3", "ESP32-S3", "hello-world"), ("esp32c3", "ESP32-C3", "freertos"), ("esp32p4", "ESP32-P4", "hello-world")];
    private static string PackArchive(string stage, string sdk, string pack, string target) =>
        Path.Combine(stage, "packs-" + sdk, $"espressif.{target}-{pack}.mcupack");
    internal static async Task OriginalAsync(string output, string workspace, Action<bool, string> check)
    {
        var tools = new ToolsetCatalog(Path.Combine(workspace, "artifacts/tool-runtime/toolsets"));
        var packs = new PackRepository(Path.Combine(output, "packs"));
        var pack = await packs.ImportAsync(Path.Combine(workspace, "artifacts/packs/Espressif-0.1.1/espressif.esp32s3-0.1.1.mcupack"));
        var root = Path.Combine(output, "project");
        await new ProjectService().CreateAsync(pack, "ESP32-S3", "hello-world", "original_sdk_regression", root);
        var report = await new BuildService(tools).BuildAsync(root, new ProgressLog("original-idf-5.5.4"));
        await JsonStore.WriteAsync(Path.Combine(output, "build.json"), report);
        check(report.Success, "original IDF 5.5.4 compiles with native compiler identity and target multilib preserved: " + (report.Success ? report.Summary : report.Log));
    }
    internal static async Task RunAsync(string output, string stage, string workspace, Action<bool, string> check)
    {
        // 原组件已在上阶段完整验收；本轮不运行原编译器，只为原工程保存其原始清单内容锁。
        var oldFingerprint = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(workspace,
            "artifacts/tool-runtime/toolsets/espressif.idf/5.5.4/toolset.json")))).ToLowerInvariant();
        var rows = new List<object>();
        foreach (var (sdk, packVersion) in Versions)
        {
            var tools = new ToolsetCatalog(Path.Combine(stage, "release-" + sdk, "runtime/toolsets"));
            var builder = new BuildService(tools);
            foreach (var (target, device, template) in Cases.Where(item => sdk == "6.1.0" || item.Target != "esp32p4"))
            {
                var name = sdk + "-" + target;
                Console.WriteLine("IDF MATRIX " + name);
                var caseRoot = Path.Combine(output, name);
                var packs = new PackRepository(Path.Combine(caseRoot, "packs"));
                var original = await packs.ImportAsync(Path.Combine(workspace, "artifacts/packs/Espressif-0.1.1", $"espressif.{target}-0.1.1.mcupack"));
                var next = await packs.ImportAsync(PackArchive(stage, sdk, packVersion, target));
                check((await packs.PruneSupersededAsync()).Removed.Count == 0 && (await packs.ListCatalogAsync()).Count == 2,
                    name + " different SDK packs survive pruning");
                var source = Path.Combine(caseRoot, "original");
                var project = await new ProjectService().CreateAsync(original, device, template, "sdk_migration", source);
                await File.AppendAllTextAsync(Path.Combine(source, project.EntryFile!), "\nvolatile unsigned sdk_version_check = 5554U;\n");
                await File.WriteAllTextAsync(Path.Combine(source, "sdkconfig"), $"CONFIG_IDF_TARGET=\"{target}\"\nCONFIG_LOG_DEFAULT_LEVEL=4\n");
                await JsonStore.WriteAsync(Path.Combine(source, ".studiox/toolchain.lock.json"), new ToolchainLock(1, "espressif.idf", "5.5.4", oldFingerprint));
                Directory.CreateDirectory(Path.Combine(source, "src/build"));
                await File.WriteAllTextAsync(Path.Combine(source, "src/build/user.txt"), "keep nested build directory\n");
                var before = await SnapshotAsync(source);
                var service = new ComponentMigrationService(packs, builder);
                var preview = await service.PreviewAsync(source, next, Path.Combine(caseRoot, "upgrade"));
                await JsonStore.WriteAsync(Path.Combine(caseRoot, "preview.json"), preview);
                check(preview.CanCreate, name + " upgrade preview: " + string.Join("; ", preview.Blockers));
                var result = await service.CreateAndBuildAsync(preview, new ProgressLog(name));
                await JsonStore.WriteAsync(Path.Combine(caseRoot, "migration-result.json"), result);
                check(result.Success, name + " real compilation: " + result.Diagnostic);
                check(before == await SnapshotAsync(source), name + " original source/config/lock bytes unchanged");
                var upgraded = await ProjectService.ReadAsync(preview.DestinationDirectory);
                var needs = await ProjectDevelopmentComponents.ReadAsync(preview.DestinationDirectory, upgraded);
                var pins = await ProjectDevelopmentComponents.ReadPinsAsync(preview.DestinationDirectory, needs);
                check(upgraded.Espressif?.SdkVersion == sdk && upgraded.ToolsetVersion == sdk && upgraded.PackVersion == packVersion && pins.Count == 1,
                    name + " exact SDK, component, pack and content lock selected");
                var resolved = await tools.ResolveAsync("espressif.idf", sdk, "esp-idf");
                await EspressifSdkIdentity.ValidateAsync(resolved, upgraded.Espressif!);
                var health = await new StudioX.Application.Health.ProjectHealthService(tools, builder).InspectAsync(preview.DestinationDirectory);
                check(!health.Checks.Any(item => item.State == StudioX.Application.Health.HealthState.Error), name + " quick health matches SDK");
                if (sdk == "6.1.0" && target == "esp32c3")
                {
                    var data = Path.Combine(caseRoot, "diagnostic-data");
                    var manager = new ToolManagementService(tools, packs, new(data), data);
                    var entry = (await manager.InspectAsync(preview.DestinationDirectory)).Versions.Single();
                    var diagnostic = await manager.DiagnoseDebugEnvironmentAsync(entry, preview.DestinationDirectory, new ProgressLog(name));
                    await File.WriteAllTextAsync(Path.Combine(caseRoot, "debug-environment.txt"), diagnostic);
                    check(diagnostic.Contains("[gdb]") && diagnostic.Contains("[openocd]"), "real IDF 6.1 GDB/OpenOCD version queries complete without hardware");
                }
                rows.Add(new { sdk, target, template, original = source, upgraded = preview.DestinationDirectory, result.Success });
                await JsonStore.WriteAsync(Path.Combine(output, "matrix.json"), rows);
            }
        }
    }
    internal static async Task ImportAndSelectAsync(string output, string stage, string workspace, Action<bool, string> check)
    {
        var data = Path.Combine(output, "user-data");
        var catalog = new ToolsetCatalog(Path.Combine(output, "runtime/toolsets"), data);
        var packs = new PackRepository(Path.Combine(data, "packs"));
        var manager = new ToolManagementService(catalog, packs, new(data), data);
        foreach (var (sdk, _) in Versions)
        {
            var archive = Path.Combine(stage, $"espressif.idf-{sdk}-win-x64.mcutoolchain");
            var preview = await manager.PreviewInstallAsync(archive, new ProgressLog(sdk));
            var installed = await manager.InstallAsync(preview, new ProgressLog(sdk));
            var duplicate = await manager.InstallAsync(await manager.PreviewInstallAsync(archive), new ProgressLog(sdk));
            check(!installed.AlreadyInstalled && duplicate.AlreadyInstalled, sdk + " actual archive import, full hash verification and duplicate import");
            await JsonStore.WriteAsync(Path.Combine(output, "import-" + sdk + ".json"), new { preview, installed, duplicate });
            await packs.ImportAsync(PackArchive(stage, sdk, Versions.Single(item => item.Sdk == sdk).Pack, "esp32s3"));
        }
        var original = await packs.ImportAsync(Path.Combine(workspace, "artifacts/packs/Espressif-0.1.1/espressif.esp32s3-0.1.1.mcupack"));
        check((await packs.PruneSupersededAsync()).Removed.Count == 0, "four exact IDF pack versions coexist");
        var service = new EspressifProjectVersionService(packs, catalog);
        var choices = await service.ListAsync(original, "ESP32-S3", "hello-world");
        check(choices.Count == 4 && choices.Count(item => item.State == EspressifVersionState.Ready) == 3 && choices.Single(item => item.SdkVersion == "5.5.4").State == EspressifVersionState.Missing,
            "version selector lists four explicit SDKs and distinguishes missing components");
        await JsonStore.WriteAsync(Path.Combine(output, "version-choices.json"), choices);
        var selected = choices.Single(item => item.SdkVersion == "6.1.0");
        await service.EnsureSelectionAsync(selected);
        var projectPath = Path.Combine(output, "selected-6.1.0");
        var project = await new ProjectService().CreateAsync(selected.Pack!, selected.DeviceId, selected.TemplateId, "selected_idf", projectPath);
        check(project.Espressif?.SdkVersion == "6.1.0" && project.ToolsetVersion == "6.1.0", "new project uses the explicitly selected IDF version");
        await catalog.SetEnabledAsync("espressif.idf", "6.1.0", false);
        check((await service.ListAsync(original, "ESP32-S3", "hello-world")).Single(item => item.SdkVersion == "6.1.0") is { State: EspressifVersionState.Disabled, CanCreate: false }, "selector exposes disabled version and blocks creation");
        try { await service.EnsureSelectionAsync(selected); throw new InvalidOperationException("stale enabled state accepted"); }
        catch (StudioXException error) { check(error.Code == "ESPRESSIF_VERSION_SELECTION", "selection rechecks enabled state before creating"); }
        var plan = await new ProjectToolPreparationService(catalog, manager).InspectAsync(projectPath);
        check(plan.Requirements.Single().State == ProjectToolState.Disabled, "existing project directs user to enable its exact version without downloading");
        await catalog.SetEnabledAsync("espressif.idf", "6.1.0", true);
        check((await new BuildService(catalog).BuildAsync(projectPath, new ProgressLog("imported-6.1.0"))).Success, "new project builds with the actually imported SDK");
        var c3pack = await packs.ImportAsync(PackArchive(stage, "6.1.0", "0.4.0", "esp32c3"));
        var c3choices = await service.ListAsync(c3pack, "ESP32-C3", "hello-world");
        check(c3choices.Count(item => item.State == EspressifVersionState.PackMissing) == 2, "installed SDK without matching chip/template pack cannot be guessed or selected for creation");
    }
    private static async Task<string> SnapshotAsync(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        { hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path))); hash.AppendData(await File.ReadAllBytesAsync(path)); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static async Task CheckImportedAsync(string output, string imported, string workspace, Action<bool, string> check)
    {
        var data = Path.Combine(imported, "user-data");
        var catalog = new ToolsetCatalog(Path.Combine(imported, "runtime/toolsets"), data);
        var packs = new PackRepository(Path.Combine(data, "packs"));
        foreach (var (sdk, _) in Versions)
        {
            using var receipt = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(imported, "import-" + sdk + ".json")));
            check(!receipt.RootElement.GetProperty("installed").GetProperty("alreadyInstalled").GetBoolean()
                && receipt.RootElement.GetProperty("duplicate").GetProperty("alreadyInstalled").GetBoolean(), sdk + " prior real import and duplicate receipt verified");
        }
        var original = (await packs.ListCatalogAsync()).Single(item => item.Manifest.Id == "espressif.esp32s3" && item.Manifest.Version == "0.1.1");
        var selector = new EspressifProjectVersionService(packs, catalog);
        var choices = await selector.ListAsync(original, "ESP32-S3", "hello-world");
        check(choices.Count == 4 && choices.Count(item => item.State == EspressifVersionState.Ready) == 3, "actual imported kits provide three ready SDK choices plus missing original version");
        var selected = choices.Single(item => item.SdkVersion == "6.1.0");
        await selector.EnsureSelectionAsync(selected);
        var root = Path.Combine(imported, "selected-6.1.0");
        var before = await SnapshotAsync(Path.Combine(workspace, "artifacts/tool-runtime/toolsets/espressif.idf/5.5.4/sdk/tools/cmake"));
        var report = await new BuildService(catalog).BuildAsync(root, new ProgressLog("imported-sdk-6.1.0"));
        await JsonStore.WriteAsync(Path.Combine(output, "build.json"), report);
        check(report.Success, "real new project builds from imported IDF 6.1 in a spaced ASCII runtime: " + (report.Success ? report.Summary : report.Log));
        check(before == await SnapshotAsync(Path.Combine(workspace, "artifacts/tool-runtime/toolsets/espressif.idf/5.5.4/sdk/tools/cmake")), "old SDK CMake files unchanged by compiler binding repair");
        var manager = new ToolManagementService(catalog, packs, new(data), data);
        var entry = (await manager.InspectAsync(null)).Versions.Single(item => item.Id == "espressif.idf" && item.Version == "6.1.0");
        var diagnostic = await manager.DiagnoseDebugEnvironmentAsync(entry, null, new ProgressLog("imported-sdk-debug"));
        await File.WriteAllTextAsync(Path.Combine(output, "debug-environment.txt"), diagnostic);
        check(new[] { "[gdb-esp32]", "[gdb-esp32s3]", "[gdb-riscv]", "[openocd]" }.All(diagnostic.Contains),
            "component management checks all declared IDF 6.1 debuggers and OpenOCD without a project or hardware");
        var stage = Path.GetDirectoryName(imported)!;
        var c3 = await packs.ImportAsync(PackArchive(stage, "6.1.0", "0.4.0", "esp32c3"));
        check((await selector.ListAsync(c3, "ESP32-C3", "hello-world")).Count(item => item.State == EspressifVersionState.PackMissing) == 2,
            "installed SDK cannot borrow another SDK pack when chip/template support is missing");
    }
    private sealed class ProgressLog(string name) : IProgress<string> { public void Report(string value) => Console.WriteLine(name + ": " + value); }

    internal static async Task CompleteMatrixAsync(string output, string runtime, string projectRoot, Action<bool, string> check)
    {
        var matrixRoot = Path.GetDirectoryName(Path.GetDirectoryName(projectRoot)!)!;
        var priorRows = (System.Text.Json.Nodes.JsonArray)System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(matrixRoot, "matrix.json")))!;
        check(priorRows.Count == 6 && priorRows.All(row => row!["success"]!.GetValue<bool>()), "six completed native SDK migration builds retain successful receipts");
        using var prior = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(matrixRoot, "result.json")));
        check(prior.RootElement.GetProperty("checks").EnumerateArray().Any(item => item.GetString() == "6.1.0-esp32p4 original source/config/lock bytes unchanged"),
            "prior P4 compilation preserved its original source, configuration and locks before cache contamination was rejected");
        var source = Path.Combine(Path.GetDirectoryName(projectRoot)!, "original");
        var before = await SnapshotAsync(source);
        var tools = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builder = new BuildService(tools);
        var report = await builder.BuildAsync(projectRoot, new ProgressLog("idf6-p4-recheck"));
        await JsonStore.WriteAsync(Path.Combine(output, "build.json"), report);
        check(report.Success, "P4 real rebuild and complete component verification pass after removing only unindexed generated GDB caches: " + (report.Success ? report.Summary : report.Log));
        check(before == await SnapshotAsync(source), "P4 recheck preserves original bytes again");
        var project = await ProjectService.ReadAsync(projectRoot);
        var resolved = await tools.ResolveAsync("espressif.idf", "6.1.0", "esp-idf");
        await EspressifSdkIdentity.ValidateAsync(resolved, project.Espressif!);
        var health = await new StudioX.Application.Health.ProjectHealthService(tools, builder).InspectAsync(projectRoot);
        check(project.Espressif is { Target: "esp32p4", SdkVersion: "6.1.0" } && !health.Checks.Any(item => item.State == StudioX.Application.Health.HealthState.Error),
            "P4 exact SDK identity and quick project health pass");
        priorRows.Add(System.Text.Json.JsonSerializer.SerializeToNode(new { sdk = "6.1.0", target = "esp32p4", template = "hello-world", original = source, upgraded = projectRoot, success = true }));
        await File.WriteAllTextAsync(Path.Combine(output, "matrix.json"), priorRows.ToJsonString(new() { WriteIndented = true }));
    }
}
