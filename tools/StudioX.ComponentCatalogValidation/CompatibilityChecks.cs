namespace StudioX.ComponentCatalogValidation;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Distribution;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class CompatibilityChecks
{
    internal static async Task RunAsync(string output, Action<bool, string> check)
    {
        var root = Path.Combine(output, "compatibility");
        Directory.CreateDirectory(root);
        var projectDirectory = Path.Combine(root, "中文 工程");
        var projectFile = Path.Combine(projectDirectory, ".studiox/project.json");
        var project = new ProjectManifest(1, "compatibility", "test.pack", "1.0.0", "fixture", "TestDevice", "minimal", "test.gcc", "1.0.0", "test-gcc");
        await JsonStore.WriteAsync(projectFile, project);
        var sdkconfig = Path.Combine(projectDirectory, "sdkconfig");
        await File.WriteAllTextAsync(sdkconfig, "preserve sdk configuration");
        var fileBytes = new byte[] { 1, 2, 3, 4 };
        var manifest = new ToolsetManifest(1, "test.gcc", "1.0.0", "win-x64", "test-gcc",
            new[] { "cmake", "ninja", "gcc", "gxx", "objcopy", "size" }.ToDictionary(role => role, _ => "probe.exe"),
            new() { ["probe.exe"] = Hash(fileBytes) }, ComponentVersions: new() { ["cmake"] = "3.30.0" });
        var catalog = new ToolsetCatalog(Path.Combine(root, "runtime/toolsets"));
        var management = new ToolManagementService(catalog, new(Path.Combine(root, "packs")), new(root), root);
        var service = new ProjectToolPreparationService(catalog, management);
        var installed = Path.Combine(catalog.RootDirectory, manifest.Id, manifest.Version);
        Directory.CreateDirectory(installed);
        var old = manifest with { Sha256 = new(manifest.Sha256) { ["include/vendor-extra.h"] = Hash(fileBytes) } };
        await JsonStore.WriteAsync(Path.Combine(installed, "toolset.json"), old);
        await File.WriteAllBytesAsync(Path.Combine(installed, "probe.exe"), fileBytes);
        Directory.CreateDirectory(Path.Combine(installed, "include"));
        await File.WriteAllBytesAsync(Path.Combine(installed, "include/vendor-extra.h"), fileBytes);
        var plan = await service.InspectAsync(projectDirectory);
        var upgrade = await management.PreviewInstallAsync(await Archive("new", manifest with { Version = "1.1.0", ComponentVersions = new() { ["cmake"] = "4.4.0" } }));
        var preview = await service.PreviewCompatibilityAsync(plan, upgrade);
        check(preview.State == ComponentProjectCompatibility.MigrationRequired && preview.CanInstall
            && preview.Differences.Any(d => d.Contains("3.30.0 → 4.4.0")) && preview.RemovedFiles == 1
            && preview.RemovedFileExamples.Contains("include/vendor-extra.h"), "same compiler with new component version still reports migration, internal tool change and removed header");
        check((await service.PreviewCompatibilityAsync(null, upgrade)).State == ComponentProjectCompatibility.NoProject,
            "component installation without a project makes no compatibility claim");
        var originalProject = await File.ReadAllBytesAsync(projectFile);
        var originalConfig = await File.ReadAllBytesAsync(sdkconfig);
        await service.InstallWithCompatibilityAsync(plan, upgrade, preview);
        check(File.Exists(Path.Combine(catalog.RootDirectory, "test.gcc/1.1.0/probe.exe")) && File.Exists(Path.Combine(installed, "include/vendor-extra.h"))
            && (await File.ReadAllBytesAsync(projectFile)).SequenceEqual(originalProject) && (await File.ReadAllBytesAsync(sdkconfig)).SequenceEqual(originalConfig)
            && !File.Exists(Path.Combine(projectDirectory, DevelopmentComponentLock.RelativePath)),
            "side-by-side install preserves old tool, project, SDK settings and uncreated locks without executing fixture tools");
        await JsonStore.WriteAsync(projectFile, project with { Name = "changed" });
        await Reject(() => service.InstallWithCompatibilityAsync(plan, upgrade, preview), "TOOLS_PROJECT_CHANGED", "project change after compatibility preview blocks install", check);
        await JsonStore.WriteAsync(projectFile, project);
        var exact = await management.PreviewInstallAsync(await Archive("exact", old));
        var exactPreview = await service.PreviewCompatibilityAsync(await service.InspectAsync(projectDirectory), exact);
        check(exactPreview.State == ComponentProjectCompatibility.ExactRequirement && exactPreview.CanInstall, "exact required component reports metadata match with full verification still pending");
        var lockFile = Path.Combine(projectDirectory, ".studiox/toolchain.lock.json");
        await JsonStore.WriteAsync(lockFile, new ToolchainLock(1, project.ToolsetId, project.ToolsetVersion, new string('f', 64)));
        await Reject(() => service.InstallWithCompatibilityAsync(plan, upgrade, preview), "TOOLS_PROJECT_CHANGED", "new content lock invalidates existing upgrade preview", check);
        var pinned = await service.PreviewCompatibilityAsync(await service.InspectAsync(projectDirectory), exact);
        check(pinned.State == ComponentProjectCompatibility.Incompatible && !pinned.CanInstall && pinned.Blockers.Any(b => b.Contains("指纹")),
            "same component version with wrong content pin cannot be used by project");
        File.Delete(lockFile);
        var emptyCatalog = new ToolsetCatalog(Path.Combine(root, "empty-runtime/toolsets"));
        var emptyManagement = new ToolManagementService(emptyCatalog, new(Path.Combine(root, "empty-packs")), new(root), root);
        var emptyService = new ProjectToolPreparationService(emptyCatalog, emptyManagement);
        var wrongCompiler = await emptyManagement.PreviewInstallAsync(await Archive("wrong-compiler", manifest with { CompilerId = "changed-abi" }));
        var wrong = await emptyService.PreviewCompatibilityAsync(await emptyService.InspectAsync(projectDirectory), wrongCompiler);
        check(wrong.State == ComponentProjectCompatibility.Incompatible && !wrong.CanInstall, "same required version with different compiler is blocked");
        var missingRole = await emptyManagement.PreviewInstallAsync(await Archive("missing-role", manifest with { Executables = new() { ["gcc"] = "probe.exe" } }));
        var roles = await emptyService.PreviewCompatibilityAsync(await emptyService.InspectAsync(projectDirectory), missingRole);
        check(roles.State == ComponentProjectCompatibility.Incompatible && roles.Blockers.Any(b => b.Contains("ninja")), "required executable roles must be indexed in candidate metadata");
        await JsonStore.WriteAsync(projectFile, project with { ToolsetId = "other.gcc" });
        check((await emptyService.PreviewCompatibilityAsync(await emptyService.InspectAsync(projectDirectory), upgrade)).State == ComponentProjectCompatibility.Unrelated,
            "unrelated component never becomes a project requirement");
        var idfProject = project with { ToolsetId = "espressif.idf", ToolsetVersion = "5.5.4", CompilerId = "esp-idf", Espressif = new("esp-idf", "esp32s3", "5.5.4") };
        await JsonStore.WriteAsync(projectFile, idfProject);
        var idf = manifest with { Id = "espressif.idf", Version = "5.5.4", CompilerId = "esp-idf", Purpose = "esp-idf",
            Executables = new[] { "python", "cmake", "ninja", "git", "gcc-esp32s3", "gxx-esp32s3", "size-esp32s3" }.ToDictionary(role => role, _ => "probe.exe"),
            ResourceDirectories = new() { ["idf"] = "sdk", ["tools"] = "tools", ["python-env"] = "python" },
            Sha256 = new(manifest.Sha256) { ["sdk/tools/idf.py"] = Hash(fileBytes), ["sdk/tools/cmake/project.cmake"] = Hash(fileBytes), ["tools/tool.bin"] = Hash(fileBytes), ["python/env.bin"] = Hash(fileBytes) } };
        var idfUpgrade = await emptyManagement.PreviewInstallAsync(await Archive("idf-upgrade", idf with { Version = "6.0.0" }));
        var idfPreview = await emptyService.PreviewCompatibilityAsync(await emptyService.InspectAsync(projectDirectory), idfUpgrade);
        check(idfPreview.State == ComponentProjectCompatibility.MigrationRequired && idfPreview.Blockers.Any(b => b.Contains("SDK 版本不同")),
            "IDF SDK version change remains explicit migration even with unchanged compiler");
        var idfWrongTarget = await emptyManagement.PreviewInstallAsync(await Archive("idf-target", idf with { Executables = new() { ["python"] = "probe.exe", ["cmake"] = "probe.exe", ["ninja"] = "probe.exe", ["git"] = "probe.exe", ["gcc-riscv"] = "probe.exe" } }));
        var target = await emptyService.PreviewCompatibilityAsync(await emptyService.InspectAsync(projectDirectory), idfWrongTarget);
        check(target.State == ComponentProjectCompatibility.Incompatible && target.Blockers.Any(b => b.Contains("gcc-esp32s3")), "IDF candidate cannot substitute another target compiler");
        await JsonStore.WriteAsync(projectFile, project);
        var uiArchive = exact.Archive;
        await JsonStore.WriteAsync(Path.Combine(root, "ui-catalog.json"), new DistributionCatalog(1, "Offline fixture", [new("tool", manifest.Id, manifest.Version,
            "Offline GCC fixture", Path.GetFileName(uiArchive), Hash(await File.ReadAllBytesAsync(uiArchive)), new FileInfo(uiArchive).Length, exact.Bytes,
            "NOASSERTION", "https://example.test/fixture", "Offline validation fixture; executable bytes are never run.")]));
        var tamperPlan = await service.InspectAsync(projectDirectory);
        await File.AppendAllTextAsync(upgrade.Archive, "tamper");
        await Reject(() => service.PreviewCompatibilityAsync(tamperPlan, upgrade), "TOOLS_CHANGED", "changed archive cannot reuse compatibility preview", check);

        async Task<string> Archive(string name, ToolsetManifest description)
        {
            var path = Path.Combine(root, name + ".mcutoolchain");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            await using (var stream = zip.CreateEntry("toolset.json").Open()) await stream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(description, JsonStore.Options));
            foreach (var relative in description.Sha256.Keys)
            { await using var stream = zip.CreateEntry(relative).Open(); await stream.WriteAsync(fileBytes); }
            return path;
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task Reject(Func<Task> action, string code, string label, Action<bool, string> check)
    {
        try { await action(); throw new InvalidOperationException(label); }
        catch (StudioXException error) { check(error.Code == code, label); }
    }
}
