namespace StudioX.EspressifValidation;

using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;
using System.Security.Cryptography;
using System.Text.Json;

internal static class EspressifProjectChecks
{
    private static int passed;

    public static async Task RunAsync(string packsDirectory, string outputDirectory, bool officialOnly = false)
    {
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Validation output must be a new directory.");
        }
        Directory.CreateDirectory(outputDirectory);
        var repository = new PackRepository(Path.Combine(outputDirectory, "repository"));
        var installed = new List<InstalledPack>();
        foreach (var archive in Directory.GetFiles(packsDirectory, "*.mcupack").Order(StringComparer.Ordinal))
        {
            installed.Add(await repository.ImportAsync(archive));
        }
        Check(installed.Count == 7, "seven independent SDK packs import successfully");
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in installed.Where(pack => !officialOnly || pack.Manifest.Devices[0].Espressif!.Framework == "esp-idf"))
        {
            var device = pack.Manifest.Devices.Single();
            var profile = device.Espressif!;
            targets.Add(profile.Target);
            Check(Directory.GetFiles(pack.RootDirectory, "*", SearchOption.AllDirectories).Length <= 22,
                device.Id + ": SDK and toolchains remain shared");
            foreach (var template in device.Templates)
            {
                var projectDirectory = Path.Combine(outputDirectory, "projects", profile.Target + "_" + template.Id.Replace('-', '_'));
                var project = await new ProjectService().CreateAsync(pack, device.Id, template.Id,
                    profile.Target + "_" + template.Id.Replace('-', '_'), projectDirectory);
                var read = await ProjectService.ReadAsync(projectDirectory);
                var rootCMake = await File.ReadAllTextAsync(Path.Combine(projectDirectory, "CMakeLists.txt"));
                var component = template.EspressifExample is null ? "src" : "main";
                var componentCMake = await File.ReadAllTextAsync(Path.Combine(projectDirectory, component, "CMakeLists.txt"));
                Check(project == read && read.Espressif == new EspressifProjectSettings(profile.Framework, profile.Target, profile.SdkVersion),
                    project.Name + ": manifest survives round trip with exact SDK target");
                Check(rootCMake.Contains("include($ENV{IDF_PATH}/tools/cmake/project.cmake)", StringComparison.Ordinal) &&
                    componentCMake.Contains("idf_component_register", StringComparison.Ordinal) &&
                    !rootCMake.Contains("firmware", StringComparison.Ordinal) &&
                    !File.Exists(Path.Combine(projectDirectory, "device", "platform.cmake")),
                    project.Name + ": native SDK component replaces bare-metal generator");
                if (profile.Framework == "esp8266-rtos-sdk")
                {
                    Check(rootCMake.Contains("set(COMPONENTS src esptool_py bootloader partition_table pthread)", StringComparison.Ordinal) &&
                        rootCMake.Contains("-Wl,-u,pthread_include_pthread_cond_var_impl", StringComparison.Ordinal),
                        project.Name + ": legacy SDK tools and real GCC pthread implementation are linked");
                }
                if (template.EspressifExample is { } example)
                {
                    Check(pack.Manifest.Version == "0.1.1" && project.EntryFile == template.EntryFile[(example.ExampleDirectory.Length + 1)..] &&
                        rootCMake.Contains("idf_build_set_property(MINIMAL_BUILD ON)", StringComparison.Ordinal) &&
                        !rootCMake.Contains("set(COMPONENTS", StringComparison.Ordinal) && !Directory.Exists(Path.Combine(projectDirectory, "src")),
                        project.Name + ": original main layout and minimal native build survive with explicit entry metadata");
                    using var evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(projectDirectory, "template-source.json")));
                    foreach (var file in evidence.RootElement.GetProperty("filesSha256").EnumerateObject())
                    {
                        if (file.Name is "CMakeLists.txt" or "sdkconfig.defaults")
                        {
                            continue;
                        }
                        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(PathBoundary.Resolve(projectDirectory, file.Name)))).ToLowerInvariant();
                        Check(hash == file.Value.GetString(), project.Name + ": original SDK bytes preserved: " + file.Name);
                    }
                    if (template.Id == "freertos")
                    {
                        var defaults = await File.ReadAllTextAsync(Path.Combine(projectDirectory, "sdkconfig.defaults"));
                        Check(defaults.StartsWith(await File.ReadAllTextAsync(Path.Combine(pack.RootDirectory, example.ExampleDirectory, "sdkconfig.defaults")), StringComparison.Ordinal) &&
                            defaults.Contains("CONFIG_FREERTOS_GENERATE_RUN_TIME_STATS=y", StringComparison.Ordinal),
                            project.Name + ": official FreeRTOS trace and run-time configuration is retained");
                    }
                }
                Check(new FileInfo(PathBoundary.Resolve(projectDirectory, project.EntryFile ?? "src/main.c")).Length > 0 &&
                    Directory.GetFiles(projectDirectory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) < 256 * 1024,
                    project.Name + ": project contains only source, metadata and small pack evidence");
                Check(!rootCMake.Contains("E:/", StringComparison.OrdinalIgnoreCase) && !rootCMake.Contains("C:/", StringComparison.OrdinalIgnoreCase),
                    project.Name + ": no developer tool path embedded in source");
            }
        }
        Check(targets.SetEquals(officialOnly ? ["esp32", "esp32p4", "esp32s3", "esp32c3", "esp32c5", "esp32c6"] :
            ["esp32", "esp32p4", "esp32s3", "esp32c3", "esp32c5", "esp32c6", "esp8266"]),
            "all requested SDK targets are represented without ambiguous suffix guessing");
        var sample = installed.Single(pack => pack.Manifest.Devices[0].Espressif!.Target == "esp32c3");
        var deviceSample = sample.Manifest.Devices[0];
        Reject(sample, deviceSample with
        {
            Espressif = null
        }, "PACK_DEVICE", "zero-size memory requires SDK metadata");
        Reject(sample, deviceSample with
        {
            Espressif = deviceSample.Espressif! with
            {
                Target = "esp8266"
            }
        },
            "PACK_ESPRESSIF_SDK", "ESP8266 cannot masquerade as an IDF 5.5 target");
        Reject(sample, deviceSample with
        {
            Espressif = deviceSample.Espressif! with
            {
                SdkVersion = "latest"
            }
        },
            "PACK_ESPRESSIF_SDK", "SDK version must be an explicit supported release, never latest");
        Reject(sample, deviceSample with
        {
            Architecture = "xtensa"
        }, "PACK_ESPRESSIF_TOOLSET", "RISC-V target cannot use Xtensa profile");
        Reject(sample, deviceSample with
        {
            ToolsetId = "arm.gcc"
        }, "PACK_ESPRESSIF_TOOLSET", "SDK and tool identity must agree");
        Reject(sample, deviceSample with
        {
            CompilerId = "riscv32-unknown-elf-gcc"
        }, "PACK_ESPRESSIF_TOOLSET", "generic GCC cannot bypass SDK lock");
        Reject(sample, deviceSample with
        {
            CpuFlags = ["-march=rv32imac"]
        }, "PACK_ESPRESSIF_BUILD", "native SDK does not silently ignore bare-metal CPU flags");
        Reject(sample, deviceSample with
        {
            LinkerScript = "linker/guessed.ld"
        }, "PACK_ESPRESSIF_BUILD", "native SDK owns linker layout");
        var reservedTemplate = deviceSample.Templates[0] with
        {
            Files = new Dictionary<string, string> { ["src/CMakeLists.txt"] = deviceSample.Templates[0].EntryFile }
        };
        Reject(sample, deviceSample with
        {
            Templates = [reservedTemplate]
        }, "PACK_ESPRESSIF_BUILD", "template cannot overwrite generated component declaration");
        var projectRoot = Path.Combine(outputDirectory, "projects", "esp32c3_hello_world");
        var manifest = await ProjectService.ReadAsync(projectRoot);
        await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox", "project.json"), manifest with
        {
            Espressif = null
        });
        await RejectProjectAsync(projectRoot, "PROJECT_ESPRESSIF_SETTINGS", "SDK project cannot lose target metadata and route into bare-metal build");
        await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox", "project.json"), manifest with
        {
            Espressif = manifest.Espressif! with
            {
                FormatVersion = 2
            }
        });
        await RejectProjectAsync(projectRoot, "PROJECT_ESPRESSIF_SETTINGS", "unknown SDK project format is rejected");
        await JsonStore.WriteAsync(Path.Combine(projectRoot, ".studiox", "project.json"), manifest);
        var wroom = installed.Single(pack => pack.Manifest.Devices[0].Id == "ESP32-WROOM-32").Manifest.Devices[0];
        Check(wroom.FlashBytes == 4 * 1024 * 1024 && wroom.RamBytes == 520 * 1024,
            "WROOM-32 module physical capacity matches official v3.8 evidence");
        var defaultConfig = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "projects", "esp32_hello_world", "sdkconfig.defaults"));
        Check(defaultConfig.Contains("CONFIG_ESPTOOLPY_FLASHSIZE_4MB=y", StringComparison.Ordinal), "verified WROOM-32 module defaults to its 4 MiB Flash");
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.txt"), $"PASS {passed}; hardware connections and SDK downloads were not performed.\n");
        Console.WriteLine($"PASS {passed}");
    }

    private static void Reject(InstalledPack pack, DeviceDefinition device, string expectedCode, string description)
    {
        try
        {
            PackValidator.Validate(pack.Manifest with
            {
                Devices = [device]
            }, pack.RootDirectory);
        }
        catch (StudioXException error) when (error.Code == expectedCode)
        {
            Check(true, description);
            return;
        }
        throw new InvalidOperationException(description + ": expected rejection " + expectedCode);
    }

    private static async Task RejectProjectAsync(string directory, string expectedCode, string description)
    {
        try
        {
            await ProjectService.ReadAsync(directory);
        }
        catch (StudioXException error) when (error.Code == expectedCode)
        {
            Check(true, description);
            return;
        }
        throw new InvalidOperationException(description + ": expected rejection " + expectedCode);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }
        passed++;
        Console.WriteLine("PASS " + description);
    }
}
