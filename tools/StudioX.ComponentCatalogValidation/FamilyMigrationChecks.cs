namespace StudioX.ComponentCatalogValidation;

using System.Security.Cryptography;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Engine.Hdl;
using StudioX.Foundation;
using StudioX.Packages;

internal static class FamilyMigrationChecks
{
    internal static async Task ExistingBoundariesAsync(string output, string completedCase, Action<bool, string> check)
    {
        var root = Path.Combine(completedCase, "升级验证 有空格");
        var project = await ProjectService.ReadAsync(root);
        var packs = new PackRepository(Path.Combine(completedCase, "packs"));
        var original = (await packs.ListCatalogAsync()).Single(p => p.Manifest.Version == project.PackVersion);
        var source = Path.Combine(output, "target-source");
        foreach (var file in Directory.EnumerateFiles(original.RootDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(original.RootDirectory, file).Replace('\\', '/');
            if (relative is "manifest.json" or "files.sha256.json") continue;
            var target = PathBoundary.Resolve(source, relative); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        var manifest = original.Manifest with { Version = "90.0.3", Devices = original.Manifest.Devices.Select(d =>
            d with { CpuFlags = d.CpuFlags.Append(d.Architecture == "arm" ? "-mfloat-abi=soft" : "-mabi=ilp32e").ToArray() }).ToArray() };
        await JsonStore.WriteAsync(Path.Combine(source, "manifest.json"), manifest);
        var archive = Path.Combine(output, manifest.Id + "-90.0.3.mcupack");
        await PackArchiveWriter.WriteAsync(source, archive);
        var next = await packs.ImportAsync(archive);
        var service = new ComponentMigrationService(packs, new(new(Path.Combine(output, "empty-tools"))));
        var blocked = await service.PreviewAsync(root, next, Path.Combine(output, "blocked-copy"));
        check(!blocked.CanCreate && blocked.Blockers.Any(b => b.Contains("ABI")), project.DeviceId + " CPU ABI change is blocked before tools or compilation");
        if (project.PinMapping is not null)
        {
            var path = Path.Combine(root, Ag32SystemSupport.HeaderPath);
            var bytes = await File.ReadAllBytesAsync(path);
            try
            {
                await File.AppendAllTextAsync(path, "\n/* manual generated edit */\n");
                var edited = await service.PreviewAsync(root, next, Path.Combine(output, "edited-copy"));
                check(!edited.CanCreate && edited.Blockers.Any(b => b.Contains("AG32_SYSTEM_MODIFIED")), project.DeviceId + " edited generated system file is preserved for review");
            }
            finally { await File.WriteAllBytesAsync(path, bytes); }
        }
    }
    private sealed record Case(string Id, string Archive, string Device, string Template, bool Logic = false);
    private static readonly Case[] Cases = [
        new("arm-hal", "STM32-0.1.1/studiox.stm32f407-0.1.1.mcupack", "STM32F407ZG", "hal"),
        new("arm-spl-rtos", "STM32-0.1.1/studiox.stm32f407-0.1.1.mcupack", "STM32F407ZG", "spl-freertos"),
        new("wch-spl", "CH32V307-0.1.3-verified/wch.ch32v307-0.1.3.mcupack", "CH32V307VCT6", "spl"),
        new("wch-rtos", "CH32V307-0.1.3-verified/wch.ch32v307-0.1.3.mcupack", "CH32V307VCT6", "spl-freertos"),
        new("riscv-xpack", "GD32-0.1.0-r5/gigadevice.gd32vf103/gigadevice.gd32vf103-0.1.0.mcupack", "GD32VF103CBT6", "spl"),
        new("esp-s3", "Espressif-0.1.1/espressif.esp32s3-0.1.1.mcupack", "ESP32-S3", "hello-world"),
        new("esp-c3-rtos", "Espressif-0.1.1/espressif.esp32c3-0.1.1.mcupack", "ESP32-C3", "freertos"),
        new("esp-p4", "Espressif-0.1.1/espressif.esp32p4-0.1.1.mcupack", "ESP32-P4", "hello-world"),
        new("esp8266", "Espressif-0.1.1/espressif.esp8266-0.1.0.mcupack", "ESP8266", "hello-world"),
        new("agm-mapping", "AG32-FreeRTOS-checkbox/studiox.preview.ag32vf303-0.1.5.mcupack", "AG32VF303CCT6", "minimal"),
        new("agm-logic-rtos", "AG32-FreeRTOS-checkbox/studiox.preview.ag32vf303-0.1.5.mcupack", "AG32VF303CCT6", "freertos-mcu", true)
    ];

    internal static async Task RunAsync(string output, string components, string workspace, string[]? selected, Action<bool, string> check)
    {
        var catalog = new ToolsetCatalog(Path.Combine(components, "runtime/toolsets"));
        var packs = new PackRepository(Path.Combine(output, "packs"));
        var builder = new BuildService(catalog);
        var service = new ComponentMigrationService(packs, builder);
        var rows = new List<object>();
        foreach (var item in Cases.Where(c => selected is null || selected.Contains(c.Id)))
        {
            Console.WriteLine("MIGRATION " + item.Id);
            var original = await packs.ImportAsync(Path.Combine(workspace, "artifacts/packs", item.Archive));
            var targetSource = Path.Combine(output, item.Id, "target-pack-source");
            foreach (var file in Directory.EnumerateFiles(original.RootDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(original.RootDirectory, file).Replace('\\', '/');
                if (relative is "manifest.json" or "files.sha256.json") continue;
                var path = PathBoundary.Resolve(targetSource, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(file, path);
            }
            var manifest = original.Manifest with { Version = "90.0.1", Devices = original.Manifest.Devices.Select(d =>
            {
                var needs = new List<DevelopmentComponentRequirement> { new(d.ToolsetId, "1.0.1", d.CompilerId) };
                if (original.Manifest.Vendor == "AGM") needs.Add(new("agm.pin-mapping", "1.0.1", "agm.ve"));
                return d with { ToolsetVersion = "1.0.1", DevelopmentComponents = needs,
                    Templates = d.Templates.Select(t => t with { DevelopmentComponents = original.Manifest.Vendor == "AGM" && item.Logic
                        ? [new("agm.logic", "1.0.1", "agm.native")] : null }).ToArray() };
            }).ToArray() };
            await JsonStore.WriteAsync(Path.Combine(targetSource, "manifest.json"), manifest);
            var archive = Path.Combine(output, item.Id, manifest.Id + "-90.0.1.mcupack");
            await PackArchiveWriter.WriteAsync(targetSource, archive);
            // 不同测试用例可使用同一包 ID 的不同显式测试修订，避免覆盖内容。
            var casePacks = new PackRepository(Path.Combine(output, item.Id, "packs"));
            original = await casePacks.ImportAsync(Path.Combine(workspace, "artifacts/packs", item.Archive));
            var target = await casePacks.ImportAsync(archive);
            service = new(casePacks, builder);
            var root = Path.Combine(output, item.Id, "原工程 有空格");
            var project = await new ProjectService().CreateAsync(original, item.Device, item.Template, "migration_check", root, enableAg32Logic: item.Logic);
            var source = PathBoundary.Resolve(root, project.EntryFile ?? "src/main.c");
            await File.AppendAllTextAsync(source, "\nvolatile unsigned long migration_counter = 1234UL;\n");
            Directory.CreateDirectory(Path.Combine(root, "src/build"));
            await File.WriteAllTextAsync(Path.Combine(root, "src/build/user-input.txt"), "Nested directory named build is user input.\n");
            await File.AppendAllTextAsync(Path.Combine(root, "CMakeLists.txt"), "\n# User maintained root configuration retained by migration\n");
            await JsonStore.WriteAsync(Path.Combine(root, ProjectBuildSettings.RelativePath), new ProjectBuildSettings());
            if (project.Espressif is { } sdkProfile)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "sdkconfig"), "CONFIG_LOG_DEFAULT_LEVEL=4\n" + (sdkProfile.Framework == "esp-idf" ? $"CONFIG_IDF_TARGET=\"{sdkProfile.Target}\"\n" : ""));
                await JsonStore.WriteAsync(Path.Combine(root, EspressifModuleSettings.RelativePath), new EspressifModuleSettings());
            }
            if (project.PinMapping is not null)
            {
                if (item.Logic)
                {
                    await JsonStore.WriteAsync(Path.Combine(root, Ag32NativeBuildSettings.RelativePath), new Ag32NativeBuildSettings(1, ["logic/user_logic.v"], ["logic"], ["MIGRATION_TEST=1"], []));
                    await File.WriteAllTextAsync(Path.Combine(root, "logic/tb_migration.v"), "module tb_migration; initial #1 $finish; endmodule\n");
                    await JsonStore.WriteAsync(Path.Combine(root, HdlSimulationSettings.RelativePath), new HdlSimulationSettings(1, ["logic/user_logic.v"], ["logic"], [], "logic/tb_migration.v", "tb_migration"));
                }
                else
                {
                    await File.WriteAllTextAsync(Path.Combine(root, "logic/pins.ve"), "HSECLK 8\nSYSCLK 200\nBUSCLK 100\nGPIO4_4 PIN_2:OUTPUT #LED1\n");
                    // 修改配置后生成过系统文件的真实形态，也须通过迁移的基准核对。
                    var originalCatalog = new ToolsetCatalog(Path.Combine(workspace, "artifacts/tool-runtime/toolsets"));
                    var configured = await new BuildService(originalCatalog).ConfigureAsync(root);
                    check(configured.Success, item.Id + " original configuration includes regenerated system support and clock aliases");
                }
            }
            var originalNeeds = await ProjectDevelopmentComponents.ReadAsync(root, project);
            if (!File.Exists(Path.Combine(root, ".studiox/toolchain.lock.json")))
            {
                var originalTools = await new ToolsetCatalog(Path.Combine(workspace, "artifacts/tool-runtime/toolsets")).ResolveAsync(project.ToolsetId, project.ToolsetVersion, project.CompilerId);
                await JsonStore.WriteAsync(Path.Combine(root, ".studiox/toolchain.lock.json"), new ToolchainLock(1, project.ToolsetId, project.ToolsetVersion, originalTools.Fingerprint));
            }
            foreach (var relative in new[] { ".build/obsolete.obj", ".git/obsolete", ".studiox/debug.json", "notes/draft.txt" })
            { var path = PathBoundary.Resolve(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, relative); }
            var destination = Path.Combine(output, item.Id, project.Espressif is null ? "升级验证 有空格" : "upgrade-check");
            if (project.Espressif is not null)
            {
                var unsupportedPath = Path.Combine(output, item.Id, "中文 新目录");
                var pathPreview = await service.PreviewAsync(root, target, unsupportedPath);
                check(!pathPreview.CanCreate && pathPreview.Blockers.Any(b => b.Contains("ESPRESSIF_PATH")) && !Directory.Exists(unsupportedPath),
                    item.Id + " unsupported new SDK path is reported before copying or tool verification");
            }
            var preview = await service.PreviewAsync(root, target, destination);
            await JsonStore.WriteAsync(Path.Combine(output, item.Id, "preview.json"), preview);
            check(preview.CanCreate, item.Id + " preview accepts only matching framework, device, template and memory: " + string.Join("; ", preview.Blockers));
            var userFiles = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(p => Path.GetRelativePath(root, p).StartsWith("src" + Path.DirectorySeparatorChar)
                || Path.GetRelativePath(root, p).StartsWith("main" + Path.DirectorySeparatorChar) || Path.GetRelativePath(root, p).StartsWith("logic" + Path.DirectorySeparatorChar)
                || Path.GetFileName(p) is "CMakeLists.txt" or "sdkconfig.defaults")
                .Where(p => !Path.GetRelativePath(root, p).StartsWith("device" + Path.DirectorySeparatorChar)).ToDictionary(p => Path.GetRelativePath(root, p), p => File.ReadAllBytes(p));
            var before = await SnapshotAsync(root);
            var result = await service.CreateAndBuildAsync(preview, new TextProgress(item.Id));
            await JsonStore.WriteAsync(Path.Combine(output, item.Id, "migration-result.json"), result);
            check(result.Success, item.Id + " actual upgraded copy builds" + (result.Success ? "" : ": " + result.Diagnostic));
            check(before == await SnapshotAsync(root), item.Id + " original project including locks and caches is byte-for-byte unchanged");
            check(userFiles.All(p => p.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(destination, p.Key)))), item.Id + " user C/Verilog/VE/CMake/defaults retained exactly");
            var copiedProject = await ProjectService.ReadAsync(destination);
            var copiedNeeds = await ProjectDevelopmentComponents.ReadAsync(destination, copiedProject);
            var newPins = await ProjectDevelopmentComponents.ReadPinsAsync(destination, copiedNeeds);
            check(copiedProject.PackVersion == "90.0.1" && newPins.Count == copiedNeeds.Count && copiedNeeds.All(n => n.Version == "1.0.1"),
                item.Id + " new copy locks all explicitly selected compiler/mapping/logic components");
            check(!File.Exists(Path.Combine(destination, ".build/obsolete.obj")) && !Directory.Exists(Path.Combine(destination, ".git")) && !File.Exists(Path.Combine(destination, ".studiox/debug.json")),
                item.Id + " no stale build/Git/debug artifacts copied");
            if (project.Espressif is not null)
            {
                check(File.ReadAllBytes(Path.Combine(root, "sdkconfig")).SequenceEqual(File.ReadAllBytes(Path.Combine(destination, ".studiox/migration-inputs/sdkconfig")))
                    && File.ReadAllBytes(Path.Combine(root, EspressifModuleSettings.RelativePath)).SequenceEqual(File.ReadAllBytes(Path.Combine(destination, EspressifModuleSettings.RelativePath))),
                    item.Id + " input sdkconfig backup and explicit module settings retained");
                var resolved = await catalog.ResolveAsync(copiedProject.ToolsetId, copiedProject.ToolsetVersion, copiedProject.CompilerId);
                if (project.Espressif.Framework == "esp-idf")
                    await Reject(() => EspressifSdkIdentity.ValidateAsync(resolved with { Manifest = resolved.Manifest with { ComponentVersions = new(resolved.Manifest.ComponentVersions!) { ["esp-idf"] = "5.5.3" } } }, copiedProject.Espressif! with { SdkVersion = "5.5.3" }),
                        "ESPRESSIF_SDK_VERSION", item.Id + " SDK payload cannot be relabelled as another release", check);
                else
                    await Reject(() => EspressifSdkIdentity.ValidateAsync(resolved, copiedProject.Espressif! with { SdkVersion = "3.5.0" }),
                        "ESPRESSIF_TOOLSET", item.Id + " separate RTOS SDK cannot silently switch version", check);
            }
            if (item.Logic)
            {
                check(File.ReadAllBytes(Path.Combine(root, Ag32NativeBuildSettings.RelativePath)).SequenceEqual(File.ReadAllBytes(Path.Combine(destination, Ag32NativeBuildSettings.RelativePath)))
                    && File.ReadAllBytes(Path.Combine(root, HdlSimulationSettings.RelativePath)).SequenceEqual(File.ReadAllBytes(Path.Combine(destination, HdlSimulationSettings.RelativePath))),
                    item.Id + " custom logic and testbench settings preserved");
                var image = await new Ag32NativeBuildService(catalog).RequireImageAsync(destination);
                check(image.ByteCount > 0 && image.ByteCount <= 102400, item.Id + " actual mapper/Supra output is verified with new component identities");
                // 再次构建验证新锁可复用，防止双指纹历史格式使第二次构建失败。
                var repeat = await builder.BuildAsync(destination);
                check(repeat.Success, item.Id + " second complete build reuses valid independent component locks");
            }
            if (project.PinMapping is not null)
            {
                var generatedFile = Path.Combine(root, Ag32SystemSupport.HeaderPath);
                var generatedBytes = await File.ReadAllBytesAsync(generatedFile);
                await File.AppendAllTextAsync(generatedFile, "\n/* manual edit must not be discarded */\n");
                var modified = await service.PreviewAsync(root, target, Path.Combine(output, item.Id, "manual-system-copy"));
                check(!modified.CanCreate && modified.Blockers.Any(b => b.Contains("AG32_SYSTEM_MODIFIED")), item.Id + " manual generated system edits block replacement");
                await File.WriteAllBytesAsync(generatedFile, generatedBytes);
            }
            var changedManifest = manifest with { Version = "90.0.2", Devices = manifest.Devices.Select(d => d.Espressif is { } esp
                ? d with { Architecture = "xtensa", Espressif = esp with { Target = esp.Target == "esp8266" ? "esp8266" : "esp32" } }
                : d with { CpuFlags = d.CpuFlags.Append(d.Architecture == "arm" ? "-mfloat-abi=soft" : "-mabi=ilp32e").ToArray() }).ToArray() };
            if (project.Espressif?.Framework != "esp8266-rtos-sdk")
            {
                await JsonStore.WriteAsync(Path.Combine(targetSource, "manifest.json"), changedManifest);
                var changedArchive = Path.Combine(output, item.Id, changedManifest.Id + "-90.0.2.mcupack");
                await PackArchiveWriter.WriteAsync(targetSource, changedArchive);
                var incompatiblePack = await casePacks.ImportAsync(changedArchive);
                var incompatible = await service.PreviewAsync(root, incompatiblePack, Path.Combine(output, item.Id, "incompatible-copy"));
                check(!incompatible.CanCreate, item.Id + " target/CPU ABI changes require manual review before copying");
            }
            var changed = project.Espressif is not null ? Path.Combine(root, "sdkconfig") : project.PinMapping is not null ? Path.Combine(root, "logic/pins.ve") : source;
            await File.AppendAllTextAsync(changed, "\n# changed after preview\n");
            var stale = preview with { DestinationDirectory = Path.Combine(output, item.Id, "stale-copy") };
            await Reject(() => service.CreateAndBuildAsync(stale), "TOOLS_PROJECT_CHANGED", item.Id + " config/source edits invalidate preview before any copy", check);
            rows.Add(new { item.Id, item.Device, item.Template, item.Logic, result.Success, result.BuildLog, components = copiedNeeds, sdk = copiedProject.Espressif, originalPreserved = true, hardware = false });
            await JsonStore.WriteAsync(Path.Combine(output, "matrix.json"), rows);
        }
        check(rows.Count > 0, "at least one explicitly selected family case completed");
        await ProfileBoundariesAsync(output, check);
    }

    private static async Task ProfileBoundariesAsync(string output, Action<bool, string> check)
    {
        EspressifPackProfile.ValidateFramework(new("esp-idf", "esp32s3", "5.4.2"));
        check(true, "IDF adapter accepts explicit 5.x release identity without downloading or claiming a real build of that release");
        await Reject(() => { EspressifPackProfile.ValidateFramework(new("esp-idf", "esp32s3", "latest")); return Task.CompletedTask; }, "PACK_ESPRESSIF_SDK", "IDF latest/range cannot replace an exact version", check);
        EspressifPackProfile.ValidateFramework(new("esp-idf", "esp32s3", "6.1.0"));
        check(true, "IDF 6.x adapter accepts an exact release identity independently of SDK payload validation");
        await Reject(() => { EspressifPackProfile.ValidateFramework(new("esp-idf", "esp32s3", "7.0.0")); return Task.CompletedTask; }, "PACK_ESPRESSIF_SDK", "unadapted IDF major requires explicit adapter work", check);
        var root = Path.Combine(output, "legacy-logic-lock");
        var mapping = new string('a', 64); var logic = new string('b', 64);
        await JsonStore.WriteAsync(Path.Combine(root, ".studiox/ag32-logic-toolchain.lock.json"), new ToolchainLock(1, "agm.logic", "1.0.0", mapping + ":" + logic));
        DevelopmentComponentRequirement[] needs = [new("agm.agrv", "1.0.0", "agrv-gcc-11.1.0"), new("agm.pin-mapping", "1.0.0", "agm.ve"), new("agm.logic", "1.0.0", "agm.native")];
        var pins = await ProjectDevelopmentComponents.ReadPinsAsync(root, needs);
        check(pins["agm.pin-mapping"] == mapping && pins["agm.logic"] == logic, "legacy aggregate logic lock validates both original fingerprints without rewriting it");
        await JsonStore.WriteAsync(Path.Combine(root, ".studiox/ag32-mapping-toolchain.lock.json"), new ToolchainLock(1, "agm.pin-mapping", "1.0.0", new string('c', 64)));
        await Reject(() => ProjectDevelopmentComponents.ReadPinsAsync(root, needs), "TOOLCHAIN_LOCK", "legacy aggregate lock conflicts cannot bypass exact component pins", check);
    }

    private static async Task Reject(Func<Task> operation, string code, string label, Action<bool, string> check)
    { try { await operation(); throw new InvalidOperationException(label); } catch (StudioXException error) { check(error.Code == code, label + ": " + error.Code); } }
    private static async Task<string> SnapshotAsync(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        { hash.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file))); hash.AppendData(await File.ReadAllBytesAsync(file)); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private sealed class TextProgress(string name) : IProgress<string> { public void Report(string value) => Console.WriteLine(name + ": " + value); }
}
