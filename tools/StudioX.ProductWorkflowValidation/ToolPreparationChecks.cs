namespace StudioX.ProductWorkflowValidation;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Distribution;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class ToolPreparationChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var data = Path.Combine(root, "tool-preparation");
        Directory.CreateDirectory(data);
        var project = Path.Combine(data, "中文 工程");
        var original = new ProjectManifest(1, "tools_fixture", "test.pack", "1.0.0", "fixture", "TestDevice", "minimal", "test.gcc", "1.0.0", "test-gcc");
        var projectFile = Path.Combine(project, ".studiox/project.json");
        await JsonStore.WriteAsync(projectFile, original);
        await File.WriteAllTextAsync(Path.Combine(project, "sdkconfig"), "preserve settings");
        var toolsets = new ToolsetCatalog(Path.Combine(data, "IDE 工具/toolsets"));
        var management = new ToolManagementService(toolsets, new PackRepository(Path.Combine(data, "packs")), new RecentProjectService(data), data);
        var service = new ProjectToolPreparationService(toolsets, management);
        check((await service.InspectAsync(null)).Requirements.Count == 0, "base onboarding without project does not guess device or compiler");
        var manifest = new ToolsetManifest(1, "test.gcc", "1.0.0", "win-x64", "test-gcc",
            new[] { "cmake", "ninja", "gcc", "gxx", "objcopy", "size" }.ToDictionary(r => r, _ => "probe.exe"),
            new()
            {
                ["probe.exe"] = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 }))
            }, "Fixture tool");
        async Task<string> Archive(string name, ToolsetManifest description)
        {
            var output = Path.Combine(data, name + ".mcutoolchain");
            using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
            await using (var content = zip.CreateEntry("toolset.json").Open())
            {
                await content.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(description, JsonStore.Options));
            }
            await using (var content = zip.CreateEntry("probe.exe").Open())
            {
                await content.WriteAsync(new byte[] { 1, 2, 3, 4 });
            }
            return output;
        }
        var archive = await Archive("test-gcc-1", manifest);
        var archiveBytes = new FileInfo(archive).Length;
        var expanded = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonStore.Options).Length + 4;
        var entry = new DistributionEntry("tool", "test.gcc", "1.0.0", "Fixture GCC", Path.GetFileName(archive),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archive))), archiveBytes, expanded,
            "NOASSERTION", "https://example.test/tools", "Local fixture only; no compiler execution");
        var catalogPath = Path.Combine(data, "catalog.json");
        using var distribution = new DistributionService(Path.Combine(data, "downloads"));
        await JsonStore.WriteAsync(catalogPath, new DistributionCatalog(1, "Fixture publisher", [entry with { Version = "1.1.0" }]));
        var listing = await distribution.ReadAsync(catalogPath);
        var plan = await service.InspectAsync(project, listing);
        check(plan.Requirements.Single() is { State: ProjectToolState.Missing, Entry: null, Version: "1.0.0" }, "newer catalog version is not substituted for project pin");
        await JsonStore.WriteAsync(catalogPath, new DistributionCatalog(1, "Fixture publisher", [entry]));
        listing = await distribution.ReadAsync(catalogPath);
        plan = await service.InspectAsync(project, listing);
        var requirement = plan.Requirements.Single();
        check(requirement.Entry == entry && requirement.DownloadText != "目录未提供此版本" && requirement.InstalledText != "—", "exact catalog entry supplies download and expanded sizes");
        var wrong = await Archive("wrong-compiler", manifest with
        {
            CompilerId = "other-gcc"
        });
        await Reject(() => service.PreviewAsync(plan, requirement, wrong, verifyCatalog: false), "TOOLS_PROJECT_IDENTITY", "offline archive with wrong compiler cannot be prepared", check);
        var downloaded = await distribution.DownloadAsync(listing, listing.Catalog.Entries.Single());
        check(downloaded.EndsWith(".mcutoolchain", StringComparison.OrdinalIgnoreCase), "development component download retains canonical archive extension");
        var preview = await service.PreviewAsync(plan, requirement, downloaded);
        check(preview.Id == requirement.Id && preview.Version == requirement.Version && service.InstallSpacePlan(preview).All(p => p.RequiredBytes >= expanded * 2), "downloaded archive is bound to project identity with install staging budget");
        await JsonStore.WriteAsync(projectFile, original with
        {
            ToolsetVersion = "2.0.0"
        });
        await Reject(() => service.InstallAsync(plan, requirement, preview), "TOOLS_PROJECT_CHANGED", "project change between preview and install prevents publication", check);
        await JsonStore.WriteAsync(projectFile, original);
        var lockPath = Path.Combine(project, ".studiox/toolchain.lock.json");
        await JsonStore.WriteAsync(lockPath, new ToolchainLock(1, original.ToolsetId, original.ToolsetVersion, new string('f', 64)));
        var locked = await service.InspectAsync(project, listing);
        await Reject(() => service.PreviewAsync(locked, locked.Requirements.Single(), downloaded), "TOOLS_PROJECT_IDENTITY", "same version with a different pinned manifest cannot replace project content", check);
        await JsonStore.WriteAsync(lockPath, new ToolchainLock(1, original.ToolsetId, original.ToolsetVersion, preview.Fingerprint));
        plan = await service.InspectAsync(project, listing);
        requirement = plan.Requirements.Single();
        var beforeProject = await File.ReadAllBytesAsync(projectFile);
        var beforeLock = await File.ReadAllBytesAsync(lockPath);
        var beforePath = Environment.GetEnvironmentVariable("PATH");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            try
            {
                await service.InstallAsync(plan, requirement, preview, token: cancel.Token);
                check(false, "cancel install");
            }
            catch (OperationCanceledException) { check(!Directory.Exists(Path.Combine(toolsets.RootDirectory, "test.gcc/1.0.0")), "cancelled preparation leaves target tool version unpublished"); }
        }
        await service.InstallAsync(plan, requirement, preview);
        var installed = await service.InspectAsync(project, listing);
        check(installed.Requirements.Single().State == ProjectToolState.Installed, "offline fixture archive installs and fast entry inspection sees exact version");
        check((await File.ReadAllBytesAsync(projectFile)).SequenceEqual(beforeProject) && (await File.ReadAllBytesAsync(lockPath)).SequenceEqual(beforeLock)
            && await File.ReadAllTextAsync(Path.Combine(project, "sdkconfig")) == "preserve settings" && Environment.GetEnvironmentVariable("PATH") == beforePath,
            "installation preserves project configuration, content lock, sdkconfig and process PATH");
        File.Move(Path.Combine(toolsets.RootDirectory, "test.gcc/1.0.0/probe.exe"), Path.Combine(data, "preserved-probe.exe"));
        check((await service.InspectAsync(project)).Requirements.Single().State == ProjectToolState.RepairNeeded, "damaged installed entry routes to repair instead of side-by-side overwrite");
        await JsonStore.WriteAsync(Path.Combine(data, "ui-fixture.json"), new
        {
            project,
            catalogPath,
            archive
        });
    }
    private static async Task Reject(Func<Task> action, string code, string label, Action<bool, string> check)
    {
        try
        {
            await action();
            check(false, label);
        }
        catch (StudioXException error) { check(error.Code == code, label); }
    }
}
