namespace StudioX.ProductWorkflowValidation;

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Health;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class DevelopmentComponentChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var data = Path.Combine(root, "development-components");
        var source = Path.Combine(data, "pack-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "main.c"), "int main(void) { for (;;) {} }\n");
        await File.WriteAllTextAsync(Path.Combine(source, "link.ld"), "MEMORY { FLASH(rx): ORIGIN=0x08000000, LENGTH=128K }\n");
        DevelopmentComponentRequirement gcc = new("test.gcc", "1.0.0", "test-gcc");
        DevelopmentComponentRequirement extra = new("test.mapper", "1.0.0", "test-mapper", Purpose: "模板代码生成");
        var template = new ProjectTemplate("minimal", "Fixture", "Metadata only; never executes fixture bytes", "main.c",
            DevelopmentComponents: [gcc, extra]);
        var device = new DeviceDefinition("test-device", "Fixture device", "arm", 0x08000000, 131072, 0x20000000, 20480,
            gcc.Id, gcc.Version, gcc.CompilerId, [], [], [], [], "link.ld", [], [], [template], DevelopmentComponents: [gcc]);
        var manifest = new PackManifest(1, "test.component-pack", "1.0.0", "Component fixture", "Fixture", [device]);
        PackValidator.Validate(manifest, source);
        check(DevelopmentComponentRequirements.ForTemplate(device, template).Count == 2, "device and template requirements deduplicate an identical primary component");
        Reject(() => PackValidator.Validate(manifest with { Devices = [device with { DevelopmentComponents = [] }] }, source),
            "DEVELOPMENT_COMPONENT_REQUIREMENT", "native pack cannot omit its primary component", check);
        Reject(() => DevelopmentComponentRequirements.ForTemplate(device, template with { DevelopmentComponents = [gcc with { Version = "2.0.0" }] }),
            "DEVELOPMENT_COMPONENT_REQUIREMENT", "template cannot silently override device component version", check);
        Reject(() => DevelopmentComponentRequirements.ForTemplate(device, template with { DevelopmentComponents = [extra with { Host = "linux-x64" }] }),
            "DEVELOPMENT_COMPONENT_REQUIREMENT", "unsupported component platform is rejected at pack validation", check);
        Reject(() => DevelopmentComponentRequirements.ForTemplate(device, template with { DevelopmentComponents = [extra, extra] }),
            "DEVELOPMENT_COMPONENT_REQUIREMENT", "duplicate component declaration in one layer is rejected", check);
        Reject(() => DevelopmentComponentRequirements.ForTemplate(device, template with { DevelopmentComponents = [extra with { Version = "latest" }] }),
            "VERSION_INVALID", "floating component versions are rejected", check);
        var micro = template with
        {
            MicroPython = new("RPI_PICO", "1.29.0"),
            DevelopmentComponents = null
        };
        check(DevelopmentComponentRequirements.ForTemplate(device, micro).Count == 0, "script template does not inherit native device components");
        Reject(() => DevelopmentComponentRequirements.ForTemplate(device, micro with { DevelopmentComponents = [extra] }),
            "DEVELOPMENT_COMPONENT_REQUIREMENT", "script template cannot silently ignore explicit native dependencies", check);

        await JsonStore.WriteAsync(Path.Combine(source, "manifest.json"), manifest);
        var archive = Path.Combine(data, "fixture.mcupack");
        await PackArchiveWriter.WriteAsync(source, archive);
        var packs = new PackRepository(Path.Combine(data, "packs"));
        var pack = await packs.ImportAsync(archive);
        var project = Path.Combine(data, "中文 工程");
        var created = await new ProjectService().CreateAsync(pack, device.Id, template.Id, "components_fixture", project);
        check(created.DevelopmentComponents is { Count: 2 } && (await ProjectService.ReadAsync(project)) == created,
            "pack import and project generation persist structurally equal requirement snapshots without installed tools");
        var roundtrip = await ProjectService.ReadAsync(project);
        check(roundtrip.GetHashCode() == created.GetHashCode() && new HashSet<ProjectManifest> { created }.Contains(roundtrip),
            "project equality and hashing remain stable after JSON roundtrip for build receipts");
        check(ProjectComponentSummary.ForSelection(pack, device.Id, template.Id, false).Contains("test.mapper 1.0.0"),
            "new project summary displays template component and precise version");
        await File.WriteAllTextAsync(Path.Combine(project, "sdkconfig"), "preserve-sdk-settings");
        var catalog = new ToolsetCatalog(Path.Combine(data, "runtime/toolsets"));
        var management = new ToolManagementService(catalog, packs, new(data), data);
        var preparation = new ProjectToolPreparationService(catalog, management);
        var builds = new BuildService(catalog);
        var health = new ProjectHealthService(catalog, builds);
        var missing = await preparation.InspectAsync(project);
        check(missing.Requirements.Count == 2 && missing.Requirements.All(r => r.State == ProjectToolState.Missing),
            "preparation lists both missing device and template components");
        var report = await health.InspectAsync(project);
        check(report.Checks.Count(c => c.Code == "TOOLSET_MISSING" && c.Action == HealthAction.Tools) == 2 && !report.CanBuild,
            "health identifies each missing exact component with a tools action");
        check((await new ToolEnvironmentService(catalog).InspectAsync(project)).Count == 2,
            "environment management includes missing template dependencies");
        var primaryArchive = await ToolArchive(data, gcc, null);
        var primaryPreview = await preparation.PreviewAsync(missing, missing.Requirements.Single(r => r.Id == gcc.Id), primaryArchive, verifyCatalog: false);
        await preparation.InstallAsync(missing, missing.Requirements.Single(r => r.Id == gcc.Id), primaryPreview, verifyCatalog: false);
        await RejectAsync(() => builds.ConfigureAsync(project), "TOOLSET_MISSING", "missing secondary component stops before invoking primary compiler", check);
        check(!Directory.Exists(Path.Combine(project, ".build")) && !File.Exists(Path.Combine(project, ".studiox/toolchain.lock.json"))
            && !File.Exists(Path.Combine(project, DevelopmentComponentLock.RelativePath)), "failed component preflight creates neither build cache nor partial tool locks");
        var extraArchive = await ToolArchive(data, extra, "hdl-native");
        var plan = await preparation.InspectAsync(project);
        var need = plan.Requirements.Single(r => r.Id == extra.Id);
        var preview = await preparation.PreviewAsync(plan, need, extraArchive, verifyCatalog: false);
        var copiedManifest = Path.Combine(project, "device/manifest.json");
        var originalPackBytes = await File.ReadAllBytesAsync(copiedManifest);
        await File.AppendAllTextAsync(copiedManifest, "\n");
        await RejectAsync(() => preparation.InstallAsync(plan, need, preview, verifyCatalog: false), "TOOLS_PROJECT_CHANGED",
            "copied pack metadata change invalidates an already reviewed import", check);
        await File.WriteAllBytesAsync(copiedManifest, originalPackBytes);
        var beforeProject = await File.ReadAllBytesAsync(Path.Combine(project, ".studiox/project.json"));
        await preparation.InstallAsync(plan, need, preview, verifyCatalog: false);
        check((await preparation.InspectAsync(project)).Requirements.All(r => r.State == ProjectToolState.Installed)
            && (await health.InspectAsync(project)).CanBuild, "exact secondary import resolves preparation and health errors without running tool programs");
        var requiredTools = await new ToolEnvironmentService(catalog).InspectAsync(project);
        check(requiredTools.Count == 2 && requiredTools.All(r => r.Required), "environment manager marks every declared component as project-required");
        check((await File.ReadAllBytesAsync(Path.Combine(project, ".studiox/project.json"))).SequenceEqual(beforeProject)
            && await File.ReadAllTextAsync(Path.Combine(project, "sdkconfig")) == "preserve-sdk-settings", "component preparation preserves generated requirement snapshot and sdkconfig");
        var changed = false;
        var progress = new ImmediateProgress(message =>
        {
            if (changed || !message.StartsWith("检查开发环境组件："))
            {
                return;
            }
            changed = true;
            File.WriteAllText(Path.Combine(project, ".studiox/project.json"),
                JsonSerializer.Serialize(created with
                {
                    Name = "changed_during_validation"
                }, JsonStore.Options));
        });
        await RejectAsync(() => builds.ConfigureAsync(project, progress), "TOOLS_PROJECT_CHANGED",
            "project change during full tool validation prevents stale lock creation and process execution", check);
        check(changed && !File.Exists(Path.Combine(project, DevelopmentComponentLock.RelativePath))
            && !File.Exists(Path.Combine(project, ".studiox/toolchain.lock.json")),
            "configuration race rejection leaves all previously absent component locks absent");
        await File.WriteAllBytesAsync(Path.Combine(project, ".studiox/project.json"), beforeProject);

        await LockAndReferenceChecks(data, project, created, manifest, gcc, extra, primaryPreview, preview, extraArchive, catalog, management, preparation, health, builds, check);
    }

    private static async Task LockAndReferenceChecks(string data, string project, ProjectManifest created, PackManifest manifest,
        DevelopmentComponentRequirement gcc, DevelopmentComponentRequirement extra, ToolArchivePreview primaryPreview, ToolArchivePreview preview,
        string extraArchive, ToolsetCatalog catalog, ToolManagementService management, ProjectToolPreparationService preparation,
        ProjectHealthService health, BuildService builds, Action<bool, string> check)
    {
        var pins = new DevelopmentComponentLock(1,
            [new(gcc.Id, gcc.Version, gcc.Host, gcc.CompilerId, primaryPreview.Fingerprint),
             new(extra.Id, extra.Version, extra.Host, extra.CompilerId, preview.Fingerprint)]);
        var componentLockPath = Path.Combine(project, DevelopmentComponentLock.RelativePath);
        var primaryLockPath = Path.Combine(project, ".studiox/toolchain.lock.json");
        await JsonStore.WriteAsync(primaryLockPath, new ToolchainLock(1, gcc.Id, gcc.Version, primaryPreview.Fingerprint));
        await JsonStore.WriteAsync(componentLockPath, pins with
        {
            Components = [pins.Components[0], pins.Components[1] with { Fingerprint = new string('f', 64) }]
        });
        var beforeLock = await File.ReadAllBytesAsync(componentLockPath);
        check((await preparation.InspectAsync(project)).Requirements.Single(r => r.Id == extra.Id).State == ProjectToolState.RepairNeeded,
            "fast preparation detects secondary manifest content-lock mismatch");
        var report = await health.InspectAsync(project);
        check(report.Checks.Any(c => c.Code == "TOOLCHAIN_LOCK" && c.ToolsetId == extra.Id) && !report.CanBuild,
            "health reports secondary content-lock mismatch with exact component identity");
        await RejectAsync(() => builds.ConfigureAsync(project), "TOOLCHAIN_LOCK", "build rejects secondary content-lock mismatch before process execution", check);
        check((await File.ReadAllBytesAsync(componentLockPath)).SequenceEqual(beforeLock) && !Directory.Exists(Path.Combine(project, ".build")),
            "content-lock failure preserves pins and does not generate a CMake cache");
        await JsonStore.WriteAsync(componentLockPath, pins);
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), created with
        {
            DevelopmentComponents = [gcc]
        });
        await RejectAsync(() => preparation.InspectAsync(project), "DEVELOPMENT_COMPONENT_REQUIREMENT", "removing snapshot dependency cannot bypass explicit pack declarations", check);
        await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), created);
        await JsonStore.WriteAsync(componentLockPath, pins with
        {
            Components = [pins.Components[0], pins.Components[0]]
        });
        await RejectAsync(() => preparation.InspectAsync(project), "TOOLCHAIN_LOCK", "duplicate aggregate pins are rejected", check);
        await JsonStore.WriteAsync(componentLockPath, pins);
        await JsonStore.WriteAsync(primaryLockPath, new ToolchainLock(1, extra.Id, extra.Version, preview.Fingerprint));
        await RejectAsync(() => preparation.InspectAsync(project), "TOOLCHAIN_LOCK", "legacy primary lock cannot point at a secondary component", check);
        await JsonStore.WriteAsync(primaryLockPath, new ToolchainLock(1, gcc.Id, gcc.Version, primaryPreview.Fingerprint));

        await JsonStore.WriteAsync(primaryLockPath, new ToolchainLock(1, gcc.Id, gcc.Version, primaryPreview.Fingerprint.ToUpperInvariant()));
        check((await preparation.InspectAsync(project)).Requirements.All(r => r.State == ProjectToolState.Installed)
            && (await health.InspectAsync(project)).CanBuild,
            "preparation and health treat uppercase and lowercase SHA-256 pins as the same unchanged content");
        await JsonStore.WriteAsync(primaryLockPath, new ToolchainLock(1, gcc.Id, gcc.Version, primaryPreview.Fingerprint));
        var extraNewer = extra with
        {
            Version = "2.0.0"
        };
        await management.InstallAsync(await management.PreviewInstallAsync(await ToolArchive(data, extraNewer, "hdl-native")));
        await management.RegisterProjectAsync(project);
        var references = await management.InspectAsync(null);
        check(references.ReferencesComplete && references.Versions.Single(v => v.Id == extra.Id && v.Version == "1.0.0") is { CanRetire: false, References.Count: > 0 },
            "closed registered project and installed pack protect old template component from cleanup despite a newer version");
        await management.ForgetProjectAsync(project);
        references = await management.InspectAsync(null);
        check(references.ReferencesComplete && references.Versions.Single(v => v.Id == extra.Id && v.Version == "1.0.0").References.Any(r => r.StartsWith("器件包：")),
            "installed template component remains protected when no project is open or registered");
        var legacy = Path.Combine(data, "legacy-project");
        await JsonStore.WriteAsync(Path.Combine(legacy, ".studiox/project.json"), created with
        {
            DevelopmentComponents = null
        });
        check((await preparation.InspectAsync(legacy)).Requirements is { Count: 1 } legacyNeeds && legacyNeeds[0].Id == gcc.Id,
            "legacy project without copied metadata continues using explicit original primary tool fields");
        await JsonStore.WriteAsync(Path.Combine(legacy, "device/manifest.json"), manifest);
        check((await preparation.InspectAsync(legacy)).Requirements.Count == 2, "pack declarations remain enforceable if a project has no requirement snapshot");
        await JsonStore.WriteAsync(Path.Combine(data, "ui-fixture.json"), new
        {
            project,
            archive = extraArchive
        });
    }

    private static async Task<string> ToolArchive(string data, DevelopmentComponentRequirement requirement, string? purpose)
    {
        byte[] payload = [1, 2, 3, 4]; // 不执行此文件；实际工具启动验证由真实 ARM 编译场景负责。
        var roles = purpose == "hdl-native" ? new[] { "mapper" } : new[] { "cmake", "ninja", "gcc", "gxx", "objcopy", "size" };
        var manifest = new ToolsetManifest(1, requirement.Id, requirement.Version, requirement.Host, requirement.CompilerId,
            roles.ToDictionary(r => r, _ => "probe.exe"), new()
            {
                ["probe.exe"] = Convert.ToHexString(SHA256.HashData(payload))
            }, Purpose: purpose);
        var path = Path.Combine(data, requirement.Id + "-" + requirement.Version + ".mcutoolchain");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        await using (var content = zip.CreateEntry("toolset.json").Open())
        {
            await content.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(manifest, JsonStore.Options));
        }
        await using (var content = zip.CreateEntry("probe.exe").Open())
        {
            await content.WriteAsync(payload);
        }
        return path;
    }

    private static void Reject(Action action, string code, string label, Action<bool, string> check)
    {
        try
        {
            action();
            check(false, label);
        }
        catch (StudioXException error) { check(error.Code == code, label); }
    }
    private static async Task RejectAsync(Func<Task> action, string code, string label, Action<bool, string> check)
    {
        try
        {
            await action();
            check(false, label);
        }
        catch (StudioXException error) { check(error.Code == code, label); }
    }
    private sealed class ImmediateProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
