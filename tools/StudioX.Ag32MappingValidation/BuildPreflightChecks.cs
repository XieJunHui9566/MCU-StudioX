using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Engine.Hdl;
using StudioX.Foundation;
using StudioX.Packages;

internal static class BuildPreflightChecks
{
    internal static async Task RunAsync(string toolsets, string directory, string archive)
    {
        var output = Path.GetFullPath(directory);
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new validation directory.");
        }
        Directory.CreateDirectory(output);
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(Path.GetFullPath(archive));
        var root = Path.Combine(output, "普通 MCU 工程");
        await new ProjectService().CreateAsync(pack, "AG32VF303CCT6", "minimal", "编译前检查", root);
        var catalog = new ToolsetCatalog(Path.GetFullPath(toolsets));
        var noTools = new ToolsetCatalog(Path.Combine(output, "absent-tools"));
        var noLicense = Path.Combine(output, "absent-license");
        var invalidLicense = Path.Combine(output, "invalid-license");
        var phases = new List<string>();
        var progress = new InlineProgress(phases.Add);
        var checks = new List<string>();
        var source = Path.Combine(root, "logic/pins.ve");
        var originalSource = await File.ReadAllBytesAsync(source);

        void Check(bool success, string message)
        {
            if (!success)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }

        async Task Reject(Func<Task> action, string code, string label)
        {
            phases.Clear();
            try
            {
                await action();
                throw new InvalidOperationException("Expected rejection: " + code);
            }
            catch (StudioXException error) when (error.Code == code)
            {
                Check(!phases.Any(phase => phase.StartsWith("检查开发环境组件", StringComparison.Ordinal) || phase is "配置 CMake" or "编译固件"),
                    label + "; rejected before MCU tool checks, CMake and GCC");
            }
        }

        await Reject(async () => await new BuildService(noTools, noLicense).BuildAsync(root, progress),
            "AG32_MAPPING_LICENSE", "Missing license takes precedence even when compiler tools are absent");
        Check((await File.ReadAllTextAsync(Path.Combine(root, ".build/studiox-build.log"))).Contains("配置 Supra 许可", StringComparison.Ordinal),
            "Preflight log includes the license configuration entry");
        Directory.CreateDirectory(invalidLicense);
        await File.WriteAllBytesAsync(Path.Combine(invalidLicense, "license.txt"), []);
        await Reject(async () => await new BuildService(noTools, invalidLicense).BuildAsync(root, progress),
            "AG32_MAPPING_LICENSE", "Empty license rejected early");
        await File.WriteAllBytesAsync(Path.Combine(invalidLicense, "license.txt"), new byte[65537]);
        await Reject(async () => await new BuildService(noTools, invalidLicense).BuildAsync(root, progress),
            "AG32_MAPPING_LICENSE", "Oversized license rejected early");

        var builder = new BuildService(catalog);
        Check(new Ag32PinMappingBuildService(catalog).LicenseConfigured, "Local license is configured; its contents are never logged");
        await Reject(async () => await builder.BuildAsync(root, progress), "AG32_MAPPING_EMPTY", "Default empty pin mapping rejected early");
        var sourceAfterPreflight = await File.ReadAllBytesAsync(source);
        Check(originalSource.SequenceEqual(sourceAfterPreflight), "Preflight preserves the user's VE source");
        await File.WriteAllTextAsync(source, "GPIO4_4 PIN_1\n");
        await Reject(async () => await builder.BuildAsync(root, progress), "AG32_MAPPING_PIN", "Invalid physical pin rejected early");
        Check(!File.Exists(Path.Combine(root, ".build/CMakeCache.txt")) && !Directory.Exists(Path.Combine(root, ".build/ag32-system")),
            "Rejected builds never started system generation or CMake");

        await File.WriteAllTextAsync(source, "HSECLK 8\nSYSCLK 200\nBUSCLK 100\nGPIO4_4 PIN_21\n");
        await Reject(async () => await new Ag32PinMappingBuildService(catalog, noLicense).BuildAsync(root),
            "AG32_MAPPING_LICENSE", "Standalone pin mapping also checks license before conversion");
        Check(!File.Exists(Path.Combine(root, ".build/ag32-mapping/pins.vx")), "Standalone missing-license build never converted the VE source");

        var custom = Path.Combine(output, "自定义逻辑工程");
        await new ProjectService().CreateAsync(pack, "AG32VF303CCT6", "minimal", "自定义逻辑检查", custom, enableAg32Logic: true);
        await Reject(async () => await new BuildService(noTools, noLicense).BuildAsync(custom, progress),
            "AG32_MAPPING_LICENSE", "Custom logic joint build also checks license before MCU work");
        var customReport = await new Ag32NativeBuildService(noTools, noLicense).BuildAsync(custom);
        Check(!customReport.Success && customReport.Log.Contains("配置 Supra 许可", StringComparison.Ordinal) &&
            !Directory.Exists(Path.Combine(custom, ".build/ag32-logic/latest/source")),
            "Standalone custom logic fails before source snapshot, conversion or synthesis");

        var configured = await new BuildService(catalog, noLicense).ConfigureAsync(root);
        Check(configured.Success && !File.Exists(Path.Combine(root, ".build/firmware.elf")),
            "Configure-only remains available without a Supra license");
        var built = await builder.BuildAsync(root);
        await File.WriteAllTextAsync(Path.Combine(output, "successful-build.log"), built.Log);
        Check(built.Success && File.Exists(Path.Combine(root, ".build/firmware.bin")) && File.Exists(Path.Combine(root, ".build/ag32-mapping/pins.bin")),
            "Valid license and pin map complete real GCC and Supra compilation");
        var firmware = Path.Combine(root, ".build/firmware.bin");
        var firmwareHash = SHA256.HashData(await File.ReadAllBytesAsync(firmware));
        var firmwareTime = File.GetLastWriteTimeUtc(firmware);
        await Reject(async () => await new BuildService(noTools, noLicense).BuildAsync(root, progress),
            "AG32_MAPPING_LICENSE", "Missing license on a previously successful project is still detected early");
        Check(!File.Exists(Path.Combine(root, ".build/studiox-build-receipt.json")) &&
            !File.Exists(Path.Combine(root, ".build/ag32-mapping/studiox-mapping-receipt.json")) &&
            !File.Exists(Path.Combine(root, ".build/ag32-mapping/pins.bin")),
            "Failed preflight invalidates both MCU and mapping download receipts");
        var firmwareAfterPreflight = await File.ReadAllBytesAsync(firmware);
        Check(firmwareTime == File.GetLastWriteTimeUtc(firmware) && firmwareHash.SequenceEqual(SHA256.HashData(firmwareAfterPreflight)),
            "Rejected rebuild never rewrites or recompiles the MCU firmware");
        Check((await File.ReadAllTextAsync(Path.Combine(root, ".build/studiox-build.log"))).Contains("AG32_MAPPING_LICENSE", StringComparison.Ordinal),
            "Preflight failure replaces the previous successful build log");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            success = true,
            count = checks.Count,
            checks,
            hardwareAccess = false
        });
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
