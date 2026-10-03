namespace StudioX.ComponentCatalogValidation;

using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class StcComponentPackChecks
{
    internal static async Task RunAsync(string output, string oldPackArchive, string componentArchive, string oldToolsetsRoot, Action<bool, string> check)
    {
        var originalArchiveHash = await HashAsync(oldPackArchive);
        var packs = new PackRepository(Path.Combine(output, "packs"));
        var oldPack = await packs.ImportAsync(oldPackArchive);
        check(oldPack.Manifest.Id == "stc.stc8" && oldPack.Manifest.Version == "0.1.0" && oldPack.Manifest.Devices.Count == 24
            && oldPack.Manifest.Devices.All(d => d.ToolsetId == "stc.sdcc" && d.ToolsetVersion == "1.0.0" && d.CompilerId == "sdcc-4.5.0-15242"),
            "original STC pack passes complete archive validation and keeps exact old component identity");
        var tools = new ToolsetCatalog(Path.Combine(output, "runtime/toolsets"));
        var management = new ToolManagementService(tools, packs, new(output), output);
        var service = new ProjectToolPreparationService(tools, management);
        var candidate = await management.PreviewInstallAsync(componentArchive);
        check(candidate.Id == "stc.sdcc" && candidate.Version == "1.0.1" && candidate.CompilerId == "sdcc-4.5.0-15242",
            "matched pack candidate explicitly requires the published SDCC 1.0.1 identity");
        var source = Path.Combine(output, "new-pack-source");
        foreach (var file in Directory.EnumerateFiles(oldPack.RootDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(oldPack.RootDirectory, file).Replace('\\', '/');
            if (relative is "manifest.json" or "files.sha256.json") continue;
            var target = PathBoundary.Resolve(source, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        var updated = oldPack.Manifest with { Version = "0.1.1", Devices = oldPack.Manifest.Devices.Select(d => d with
            { ToolsetVersion = candidate.Version, DevelopmentComponents = [new(candidate.Id, candidate.Version, candidate.CompilerId, Purpose: "STC 8 位工程编译")] }).ToArray() };
        await JsonStore.WriteAsync(Path.Combine(source, "manifest.json"), updated);
        var archive = Path.Combine(output, "stc.stc8-0.1.1.mcupack");
        await PackArchiveWriter.WriteAsync(source, archive);
        var newPack = await packs.ImportAsync(archive);
        check(newPack.Manifest.Devices.All(d => DevelopmentComponentRequirements.ForTemplate(d, d.Templates.Single()).Single().Version == "1.0.1"),
            "new device pack imports with explicit component requirement for all 24 devices");
        await management.InstallAsync(candidate);
        var legacyProject = Path.Combine(output, "old-project");
        var oldDevice = oldPack.Manifest.Devices.First();
        await new ProjectService().CreateAsync(oldPack, oldDevice.Id, oldDevice.Templates.Single().Id, "legacy", legacyProject);
        var legacyFile = Path.Combine(legacyProject, ".studiox/project.json");
        var legacyHash = await HashAsync(legacyFile);
        var oldCatalog = new ToolsetCatalog(oldToolsetsRoot);
        var oldManagement = new ToolManagementService(oldCatalog, packs, new(output), output);
        var oldService = new ProjectToolPreparationService(oldCatalog, oldManagement);
        var oldCandidate = await oldManagement.PreviewInstallAsync(componentArchive);
        var legacyPreview = await oldService.PreviewCompatibilityAsync(await oldService.InspectAsync(legacyProject), oldCandidate);
        await JsonStore.WriteAsync(Path.Combine(output, "legacy-upgrade-preview.json"), legacyPreview);
        check(legacyPreview.State == ComponentProjectCompatibility.MigrationRequired && legacyPreview.RemovedFiles > 0
            && legacyPreview.RemovedFileExamples.Any(p => p.EndsWith(".h", StringComparison.OrdinalIgnoreCase)),
            "real SDCC old-to-new preview identifies removed local header files and does not declare automatic compatibility");
        var builder = new BuildService(tools);
        try { await builder.BuildAsync(legacyProject); throw new InvalidOperationException("Old requirement accepted new component"); }
        catch (StudioXException error) when (error.Code == "TOOLSET_MISSING")
        {
            check(await HashAsync(legacyFile) == legacyHash && !File.Exists(Path.Combine(legacyProject, DevelopmentComponentLock.RelativePath)),
                "new component alone cannot build or relock an old pinned STC project");
        }
        var rows = new List<object>();
        foreach (var device in newPack.Manifest.Devices)
        {
            var directory = Path.Combine(output, "projects", device.Id);
            await new ProjectService().CreateAsync(newPack, device.Id, device.Templates.Single().Id, "component_check", directory);
            var plan = await service.InspectAsync(directory);
            var compatible = await service.PreviewCompatibilityAsync(plan, candidate);
            if (compatible.State != ComponentProjectCompatibility.ExactRequirement) throw new InvalidOperationException(compatible.ToText());
            var report = await builder.BuildAsync(directory);
            if (!report.Success) throw new InvalidOperationException(report.Log);
            var pin = await JsonStore.ReadAsync<DevelopmentComponentLock>(Path.Combine(directory, DevelopmentComponentLock.RelativePath));
            var ihx = Path.Combine(directory, ".build/firmware.ihx");
            if (!File.Exists(ihx) || pin.Components.Single().Version != "1.0.1" || !pin.Components.Single().Fingerprint.Equals(candidate.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Generated project did not lock the exact imported component.");
            rows.Add(new { device = device.Id, component = "stc.sdcc/1.0.1", firmwareSha256 = await HashAsync(ihx), buildLog = report.LogPath });
            Console.WriteLine("BUILD " + device.Id);
        }
        await JsonStore.WriteAsync(Path.Combine(output, "build-matrix.json"), rows);
        check(rows.Count == 24, "all 24 generated STC projects compile with public component and establish exact component content locks");
        check(await HashAsync(oldPackArchive) == originalArchiveHash && await HashAsync(legacyFile) == legacyHash,
            "old published pack and legacy project bytes remain unchanged");
        await JsonStore.WriteAsync(Path.Combine(output, "matched-pack.json"), new { id = updated.Id, version = updated.Version, archive, sha256 = await HashAsync(archive), component = candidate.Identity, hardware = false, published = false });
    }
    private static async Task<string> HashAsync(string file) { await using var stream = File.OpenRead(file); return Convert.ToHexString(await SHA256.HashDataAsync(stream)); }
}
