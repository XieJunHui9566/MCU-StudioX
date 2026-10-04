namespace StudioX.ProjectHealthValidation;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Health;
using StudioX.Engine;
using StudioX.Foundation;

internal static class AnalysisEnvironmentChecks
{
    internal static async Task RunAsync(string root, ProjectManifest project, ToolsetCatalog catalog,
        ProjectHealthService service, Action<bool, string> check)
    {
        var toolRoot = Path.Combine(catalog.RootDirectory, project.ToolsetId, project.ToolsetVersion);
        var tools = new ResolvedToolset(await JsonStore.ReadAsync<ToolsetManifest>(Path.Combine(toolRoot, "toolset.json")), toolRoot, "offline-fixture")
            .ForEspressifTarget(project.Espressif!.Target);
        var build = Path.Combine(root, ".build");
        Directory.CreateDirectory(build);
        var database = Path.Combine(build, "compile_commands.json");
        var report = await service.InspectAsync(root);
        check(report.Checks.Any(item => item.Code == "HEALTH_ANALYSIS" && item.Action == HealthAction.Configure), "unconfigured SDK has an explicit configure action");
        async Task Database(string working, string compiler) => await JsonStore.WriteAsync(database, new[] { new {
            directory = working, file = Path.Combine(root, "src/main.c"), arguments = new[] { compiler, "-c", Path.Combine(root, "src/main.c") } } });
        await Database(root, tools.Tool("gcc"));
        var inputs = new[] { ".studiox/project.json", "sdkconfig", ".build/compile_commands.json" }
            .ToDictionary(relative => relative, relative => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative)))));
        var normal = await AnalysisEnvironmentInspector.InspectAsync(root, project, tools);
        check(normal.CanUseDatabase && normal.TranslationUnits == 1, "analysis accepts an owned native database with the locked compiler");
        check(inputs.All(pair => pair.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, pair.Key))))), "analysis inspection leaves project, SDK configuration and native database unchanged");
        await Database(Path.GetDirectoryName(root)!, tools.Tool("gcc"));
        report = await service.InspectAsync(root);
        check(report.Checks.Any(item => item.Code == "LANGUAGE_DATABASE_PROJECT" && item.Action == HealthAction.ResetCache && item.RawDiagnostic.Contains("directory=")), "copied database explains its old project path and offers cache preview");
        await Database(root, Path.Combine(root, "old-runtime/gcc.exe"));
        report = await service.InspectAsync(root);
        check(report.Checks.Any(item => item.Code == "LANGUAGE_DATABASE_TOOLSET" && item.RawDiagnostic.Contains("old-runtime")), "database from another compiler is distinguished from missing source headers");
        await Database(root, tools.Tool("gcc"));
        var config = Path.Combine(build, "config", "sdkconfig.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        async Task Config(object value) => await File.WriteAllTextAsync(config, JsonSerializer.Serialize(value));
        await Config(new
        {
            IDF_TARGET = "esp32",
            LIBC_PICOLIBC = false
        });
        report = await service.InspectAsync(root);
        check(report.Checks.Any(item => item.Code == "LANGUAGE_CACHE_TARGET" && item.State == HealthState.Warning), "generated target mismatch pauses analysis without inventing a firmware build failure");
        await Config(new
        {
            IDF_TARGET = "esp32s3",
            LIBC_PICOLIBC = true
        });
        report = await service.InspectAsync(root);
        check(report.Checks.Any(item => item.Code == "LANGUAGE_ESPRESSIF_LIBC" && item.Action == HealthAction.Tools && item.ToolsetVersion == "5.5.4"), "missing selected libc points to the exact locked tool version");
        await Config(new
        {
            IDF_TARGET = "esp32s3",
            LIBC_PICOLIBC = false,
            TEST_BOOL = true,
            TEST_NUMBER = 16,
            TEST_STRING = "fixture"
        });
        await File.WriteAllTextAsync(Path.Combine(root, "sdkconfig"), "CONFIG_IDF_TARGET=\"esp32s3\"\nCONFIG_TEST_BOOL=y\nCONFIG_TEST_NUMBER=0x10\nCONFIG_TEST_STRING=\"fixture\"\nCONFIG_OLD_ALIAS=y\n");
        check((await AnalysisEnvironmentInspector.InspectAsync(root, project, tools)).CanUseDatabase, "sdkconfig booleans, hex numbers, strings and deprecated aliases compare without guessed migration");
        await File.WriteAllTextAsync(Path.Combine(root, "sdkconfig"), "CONFIG_IDF_TARGET=\"esp32s3\"\n# CONFIG_TEST_BOOL is not set\n");
        report = await service.InspectAsync(root);
        check(report.Checks.Any(item => item.Code == "LANGUAGE_SDKCONFIG_CHANGED" && item.Action == HealthAction.Configure), "edited sdkconfig offers reconfiguration before stale generated macros are used");
        await File.WriteAllTextAsync(Path.Combine(root, "sdkconfig"), "CONFIG_IDF_TARGET=\"esp32s3\"\n");
        var identity = Path.Combine(build, "studiox-idf-runtime.json");
        await JsonStore.WriteAsync(identity, new
        {
            projectDirectory = root,
            toolsetDirectory = tools.RootDirectory,
            sdk = project.Espressif,
            buildSettings = new ProjectBuildSettings(),
            moduleSettings = new EspressifModuleSettings()
        });
        await JsonStore.WriteAsync(Path.Combine(root, ProjectBuildSettings.RelativePath), new ProjectBuildSettings(Optimization: CompilerOptimization.O0));
        report = await service.InspectAsync(root);
        check(report.Checks.Any(item => item.Code == "LANGUAGE_CONFIG_CHANGED" && item.Action == HealthAction.Configure), "changed engineering settings have a concrete configuration refresh action");
        await JsonStore.WriteAsync(Path.Combine(root, ProjectBuildSettings.RelativePath), new ProjectBuildSettings());
        await JsonStore.WriteAsync(identity, new
        {
            projectDirectory = root,
            toolsetDirectory = Path.Combine(root, "old-tools"),
            sdk = project.Espressif
        });
        check((await AnalysisEnvironmentInspector.InspectAsync(root, project, tools)).Issues.Any(item => item.Code == "LANGUAGE_CACHE_TOOLSET"), "runtime relocation is detected even when the old compiler still exists");
        await File.WriteAllTextAsync(database, "{ broken json");
        report = await service.InspectAsync(root);
        check(report.Checks.Any(item => item.Code == "LANGUAGE_DATABASE_INVALID" && item.RawDiagnostic.Contains("Json")), "malformed database retains its raw parser exception");
        await Database(root, tools.Tool("gcc"));
        await JsonStore.WriteAsync(identity, new
        {
            projectDirectory = root,
            toolsetDirectory = tools.RootDirectory,
            sdk = project.Espressif
        });
        check((await AnalysisEnvironmentInspector.InspectAsync(root, project, tools)).CanUseDatabase, "repaired inputs restore native analysis readiness");
        var flags = Path.Combine(build, "toolchain", "cflags");
        Directory.CreateDirectory(Path.GetDirectoryName(flags)!);
        Directory.CreateDirectory(Path.Combine(build, "specs"));
        await File.WriteAllTextAsync(flags, "-DTEST_RESPONSE=1\n");
        await File.WriteAllTextAsync(Path.Combine(build, "specs", "picolibc.specs"), "retained old specs");
        var bootFlags = Path.Combine(build, "bootloader", "toolchain", "cflags");
        Directory.CreateDirectory(Path.GetDirectoryName(bootFlags)!);
        Directory.CreateDirectory(Path.Combine(build, "bootloader", "specs"));
        await File.WriteAllTextAsync(bootFlags, "-specs=\"" + Path.Combine(root, "old-build", "picolibc.specs").Replace('\\', '/') + "\"\n");
        check((await service.InspectAsync(root)).Checks.Any(item => item.Code == "LANGUAGE_CACHE_SPECS" && item.Action == HealthAction.ResetCache), "bootloader response files expose stale absolute specs paths before native compilation");
        await File.WriteAllTextAsync(bootFlags, "-DTEST_BOOT=1\n");
        await JsonStore.WriteAsync(database, new[] { new { directory = root, file = Path.Combine(root, "src/main.c"), arguments = new[] { tools.Tool("gcc"), "@" + flags, "-c", Path.Combine(root, "src/main.c") } } });
        var response = await AnalysisEnvironmentInspector.InspectAsync(root, project, tools);
        check(response.CanUseDatabase && response.ResponseFiles.Contains(flags), "response arguments are read as bounded data and their paths remain observable");
        await File.WriteAllTextAsync(flags, "@\"" + flags.Replace('\\', '/') + "\"\n");
        check((await service.InspectAsync(root)).Checks.Any(item => item.Code == "LANGUAGE_ESPRESSIF_RESPONSE" && item.RawDiagnostic.Contains("循环")), "response recursion is diagnosed without unbounded expansion");
        var repairPlan = await service.PreviewCacheRepairAsync(root);
        check(repairPlan.Entries.Any(entry => entry.RelativePath == ".build/toolchain") && repairPlan.Entries.Any(entry => entry.RelativePath == ".build/specs") &&
            repairPlan.Entries.Any(entry => entry.RelativePath == ".build/bootloader/toolchain"), "SDK repair preview includes app and bootloader generated flags and specs");
        var backup = await service.RepairCacheAsync(repairPlan);
        check(File.Exists(Path.Combine(backup, "toolchain", "cflags")) && await File.ReadAllTextAsync(Path.Combine(backup, "specs", "picolibc.specs")) == "retained old specs", "SDK parameter repair preserves original response and specs content");
    }
}
