namespace StudioX.ComponentCatalogValidation;

using System.Security.Cryptography;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class MigrationChecks
{
    internal static async Task RunAsync(string output, string previous, Action<bool, string> check)
    {
        var priorPacks = await new PackRepository(Path.Combine(previous, "packs")).ListCatalogAsync();
        var oldInput = priorPacks.Single(p => p.Manifest.Version == "0.1.0");
        var originalArchive = Path.Combine(output, "stc.stc8-0.1.0.mcupack");
        await PackArchiveWriter.WriteAsync(oldInput.RootDirectory, originalArchive);
        var packs = new PackRepository(Path.Combine(output, "packs"));
        var oldPack = await packs.ImportAsync(originalArchive);
        var nextPack = await packs.ImportAsync(Path.Combine(previous, "stc.stc8-0.1.1.mcupack"));
        var builder = new BuildService(new ToolsetCatalog(Path.Combine(previous, "runtime/toolsets")));
        var migration = new ComponentMigrationService(packs, builder);
        var root = Path.Combine(output, "original-project");
        var device = oldPack.Manifest.Devices.First();
        await new ProjectService().CreateAsync(oldPack, device.Id, device.Templates.Single().Id, "migration_check", root);
        var main = Path.Combine(root, "src/main.c");
        await File.AppendAllTextAsync(main, "\nvolatile unsigned long migration_counter = 1234UL;\n");
        var cmake = Path.Combine(root, "CMakeLists.txt");
        await File.AppendAllTextAsync(cmake, "\ntarget_compile_definitions(firmware PRIVATE MIGRATION_CHECK=1)\n");
        var settings = new ProjectBuildSettings(Optimization: CompilerOptimization.O0);
        await JsonStore.WriteAsync(Path.Combine(root, ProjectBuildSettings.RelativePath), settings);
        var clock = new StcIspSettings(Port: "COM4", ClockMode: StcClockMode.InternalRc, ClockFrequencyHz: 11059200);
        await JsonStore.WriteAsync(Path.Combine(root, StcIspSettings.RelativePath), clock);
        await JsonStore.WriteAsync(Path.Combine(root, ".studiox/toolchain.lock.json"), new ToolchainLock(1, "stc.sdcc", "1.0.0", new string('a', 64)));
        foreach (var relative in new[] { ".build/do-not-copy.obj", ".git/do-not-copy", ".studiox/debug.json", "notes/draft.txt" })
        { var path = PathBoundary.Resolve(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, relative); }
        var destination = Path.Combine(output, "validated-copy");
        var preview = await migration.PreviewAsync(root, nextPack, destination);
        check(preview.CanCreate && preview.TargetProject.ToolsetVersion == "1.0.1" && preview.Files >= 4, "migration preview preserves explicit device/template and shows separate new component identity");
        check((await migration.ListTargetsAsync(root)).Single().Manifest.Version == "0.1.1", "migration target list includes explicitly installed same-device and same-template pack version");
        var idfRoot = Path.Combine(output, "idf-profile-only");
        await JsonStore.WriteAsync(Path.Combine(idfRoot, ".studiox/project.json"), new ProjectManifest(1, "idf_profile", "test.idf", "1.0.0", "fixture", "ESP32S3", "native",
            "espressif.idf", "5.5.4", "esp-idf", Espressif: new("esp-idf", "esp32s3", "5.5.4")));
        check((await migration.ListTargetsAsync(idfRoot)).Count == 0, "IDF uses the migration service without a startup SDK scan or target guessing");
        var mainBytes = await File.ReadAllBytesAsync(main);
        await File.AppendAllTextAsync(main, "\n/* edit after preview */\n");
        await Reject(() => migration.CreateAndBuildAsync(preview), "TOOLS_PROJECT_CHANGED", "source edit after migration preview is rejected before creating destination", check);
        check(!Directory.Exists(destination), "stale migration does not publish a partial target directory");
        await File.WriteAllBytesAsync(main, mainBytes);
        await Reject(() => migration.PreviewAsync(root, nextPack, Path.Combine(root, "nested-copy")), "TOOLS_MIGRATION_PATH", "migration cannot create a copy inside the original project", check);
        await Reject(() => migration.PreviewAsync(root, nextPack, output), "TOOLS_MIGRATION_PATH", "migration cannot choose an ancestor of original project", check);
        var occupied = Path.Combine(output, "occupied"); Directory.CreateDirectory(occupied);
        await File.WriteAllTextAsync(Path.Combine(occupied, "keep.txt"), "keep");
        await Reject(() => migration.PreviewAsync(root, nextPack, occupied), "PROJECT_EXISTS", "migration refuses an occupied destination without deleting contents", check);
        var header = Directory.EnumerateFiles(Path.Combine(root, "device/sdk/include"), "*.h").First();
        var headerBytes = await File.ReadAllBytesAsync(header);
        await File.AppendAllTextAsync(header, "\n/* user SDK change */\n");
        var modified = await migration.PreviewAsync(root, nextPack, Path.Combine(output, "sdk-modified-copy"));
        check(!modified.CanCreate && modified.Blockers.Any(b => b.Contains("已改变")), "edited vendor/device support is surfaced for manual review rather than overwritten");
        await Reject(() => migration.CreateAndBuildAsync(modified), "TOOLS_MIGRATION_REVIEW", "device-file changes prevent automated copy", check);
        await File.WriteAllBytesAsync(header, headerBytes);
        preview = await migration.PreviewAsync(root, nextPack, destination);
        var targetTemplate = Path.Combine(nextPack.RootDirectory, nextPack.Manifest.Devices.First().Templates.Single().EntryFile.Replace('/', Path.DirectorySeparatorChar));
        var targetBytes = await File.ReadAllBytesAsync(targetTemplate);
        await File.AppendAllTextAsync(targetTemplate, "\n/* changed after selection */\n");
        await Reject(() => migration.CreateAndBuildAsync(preview), "PACK_HASH", "target pack change after migration preview is rejected before copying", check);
        await File.WriteAllBytesAsync(targetTemplate, targetBytes);
        check(!Directory.Exists(destination), "changed target pack leaves destination uncreated");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try { await migration.CreateAndBuildAsync(preview, token: cancel.Token); throw new InvalidOperationException("Cancellation ignored"); }
            catch (OperationCanceledException) { check(!Directory.Exists(destination), "cancellation before creation leaves original and destination untouched"); }
        }
        var before = await Snapshot(root);
        var result = await migration.CreateAndBuildAsync(preview);
        await JsonStore.WriteAsync(Path.Combine(output, "migration-preview.json"), preview);
        if (!result.Success) throw new InvalidOperationException(result.ToText());
        check(await Snapshot(root) == before, "successful clone and actual build preserve every original file including old lock and cache");
        var project = await ProjectService.ReadAsync(destination);
        var locked = await JsonStore.ReadAsync<DevelopmentComponentLock>(Path.Combine(destination, DevelopmentComponentLock.RelativePath));
        check(project.ToolsetVersion == "1.0.1" && locked.Components.Single().Version == "1.0.1"
            && (await File.ReadAllBytesAsync(Path.Combine(destination, "src/main.c"))).SequenceEqual(mainBytes)
            && await File.ReadAllTextAsync(Path.Combine(destination, "CMakeLists.txt")) == await File.ReadAllTextAsync(cmake)
            && await JsonStore.ReadAsync<ProjectBuildSettings>(Path.Combine(destination, ProjectBuildSettings.RelativePath)) == settings
            && await StcIspSettings.ReadAsync(destination) == clock,
            "actual migration build retains user code, root CMake, optimization and clock configuration and creates new exact locks");
        check(!File.Exists(Path.Combine(destination, ".build/do-not-copy.obj")) && !Directory.Exists(Path.Combine(destination, ".git"))
            && !File.Exists(Path.Combine(destination, ".studiox/debug.json")) && File.Exists(Path.Combine(destination, "notes/draft.txt")),
            "migration skips stale build/Git/debug state while keeping custom user files");
        await File.AppendAllTextAsync(main, "\nthis is deliberately invalid C;\n");
        var badDestination = Path.Combine(output, "failed-copy");
        var badPreview = await migration.PreviewAsync(root, nextPack, badDestination);
        var badBefore = await Snapshot(root);
        var failed = await migration.CreateAndBuildAsync(badPreview);
        check(!failed.Success && !failed.Cancelled && !string.IsNullOrWhiteSpace(failed.Diagnostic)
            && File.Exists(Path.Combine(badDestination, "src/main.c")) && File.Exists(Path.Combine(badDestination, ".studiox/component-migration-result.json"))
            && await Snapshot(root) == badBefore, "compile failure keeps a repairable copy and original tool diagnostic without changing original project");
        await JsonStore.WriteAsync(Path.Combine(output, "successful-result.json"), result);
        await JsonStore.WriteAsync(Path.Combine(output, "failed-result.json"), failed);
    }
    private static async Task<string> Snapshot(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        { hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file))); hash.AppendData(await File.ReadAllBytesAsync(file)); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static async Task Reject(Func<Task> action, string code, string label, Action<bool, string> check)
    {
        try { await action(); throw new InvalidOperationException(label); }
        catch (StudioXException error) { check(error.Code == code, label); }
    }
}
