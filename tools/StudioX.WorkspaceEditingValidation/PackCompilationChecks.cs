using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class PackCompilationChecks
{
    public static async Task RunAsync(string runtime, string fixture, string output, Action<bool, string> check)
    {
        var project = Path.Combine(output, "ordinary pack 中文 with spaces");
        foreach (var relative in new[] { ".studiox/project.json", "device/manifest.json" })
        {
            var target = Path.Combine(project, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(fixture, relative), target);
        }
        // 模板宏故意与实际目标冲突，确保读取数据库后不会重新混入模板参数。
        var packPath = Path.Combine(project, "device/manifest.json");
        var pack = await JsonStore.ReadAsync<PackManifest>(packPath);
        await JsonStore.WriteAsync(packPath, pack with
        {
            Devices = [pack.Devices[0] with { Defines = ["TEMPLATE_ONLY=1"] }]
        });
        foreach (var directory in new[] { "src", "include", "Hardware 中文", ".build" })
        {
            Directory.CreateDirectory(Path.Combine(project, directory));
        }
        const string path = "src/main.c";
        const string headerPath = "Hardware 中文/BH1750.h";
        const string header = "#if USER_FEATURE != 7\n#error Incorrect actual CMake defines\n#endif\n#ifdef TEMPLATE_ONLY\n#error Template macro leaked into configured target\n#endif\ntypedef enum { BH1750_OK = 0 } BH1750_Status;\nBH1750_Status BH1750_Init(void);\n";
        const string text = "#include \"BH1750.h\"\nint read_sensor(void) { return BH1750_Init(); }\n";
        const string other = "#if USER_FEATURE != 8\n#error Incorrect per-file CMake defines\n#endif\nint another_target = USER_FEATURE;\n";
        const string cpp = "template<class T> concept Value = requires(T value) { value + 1; };\nstatic_assert(Value<int>);\n";
        await File.WriteAllTextAsync(Path.Combine(project, path), text);
        await File.WriteAllTextAsync(Path.Combine(project, headerPath), header);
        await File.WriteAllTextAsync(Path.Combine(project, "src/other.c"), other);
        await File.WriteAllTextAsync(Path.Combine(project, "src/value.cpp"), cpp);
        check(!File.Exists(Path.Combine(project, ".studiox/keil-import.json")), "ordinary Pack fixture has no Keil marker or CubeMX metadata");
        var database = Path.Combine(project, ".build/compile_commands.json");
        var response = Path.Combine(project, ".build/target.rsp");
        const string targetFlags = "-mcpu=cortex-m3 -mthumb -DUSER_FEATURE=7 -I\"../Hardware 中文\" -std=gnu11";
        await File.WriteAllTextAsync(response, targetFlags);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "language"));
        await service.StartAsync(project, token);
        check(service.StatusDescription.Contains("器件模板", StringComparison.Ordinal) && service.StatusDescription.Contains("配置", StringComparison.Ordinal),
            "unconfigured ordinary Pack labels template analysis and explains how to sync CMake");
        var entries = new[]
        {
            new { directory = Path.Combine(project, ".build"), file = "../" + path, command = "arm-none-eabi-gcc @target.rsp -c ../" + path + " -o sensor.o" },
            new { directory = Path.Combine(project, ".build"), file = "../src/other.c", command = "arm-none-eabi-gcc -mcpu=cortex-m3 -mthumb -DUSER_FEATURE=8 -c ../src/other.c" },
            new { directory = Path.Combine(project, ".build"), file = "../src/value.cpp", command = "arm-none-eabi-g++ -mcpu=cortex-m3 -mthumb -std=gnu++20 -c ../src/value.cpp" }
        };
        await JsonStore.WriteAsync(database, entries, token);
        check(await service.RefreshEnvironmentAsync(token), "first CMake database switches a running ordinary Pack from template to real commands");
        async Task<CodeDiagnosticBatch> Analyze(string source)
        {
            await service.SynchronizeDiagnosticsAsync(path, source,
                [new(path, source), new(headerPath, header), new("src/other.c", other), new("src/value.cpp", cpp)], token);
            for (var attempt = 0; attempt < 150; attempt++)
            {
                if (service.GetDiagnostics().SingleOrDefault(batch => batch.Path == path && batch.Text == source) is { } batch &&
                    service.GetDiagnostics().Count == 4)
                {
                    return batch;
                }
                await Task.Delay(40, token);
            }
            throw new InvalidOperationException("Missing ordinary Pack diagnostics: " + string.Join('\n', service.DrainLog()));
        }
        var initial = await Analyze(text);
        await JsonStore.WriteAsync(Path.Combine(output, "initial-diagnostics.json"), service.GetDiagnostics(), token);
        check(initial.IsComplete && initial.Items.All(item => item.Severity != 1), "ordinary Pack resolves custom Hardware header using CMake response includes and macros");
        check(service.GetDiagnostics().All(batch => batch.IsComplete && batch.Items.All(item => item.Severity != 1)),
            "separate C macros and C++20 standard follow actual per-file commands without template overrides");
        var locations = await service.NavigateAsync(path, text, text.IndexOf("BH1750_Init", StringComparison.Ordinal), false, token);
        check(locations.Any(location => location.DocumentPath == headerPath), "ordinary Pack can navigate declarations in user-added header directories");
        var broken = text.Replace("BH1750_Init()", "missing_sensor()", StringComparison.Ordinal);
        check((await Analyze(broken)).Items.Any(item => item.Severity == 1 && item.Message.Contains("missing_sensor", StringComparison.Ordinal)),
            "actual undefined functions remain errors rather than being suppressed");
        await File.WriteAllTextAsync(response, targetFlags.Replace("USER_FEATURE=7", "USER_FEATURE=9", StringComparison.Ordinal), token);
        await service.RefreshEnvironmentAsync(token);
        check(service.GetDiagnostics().Count == 0, "changed CMake response revokes stale ordinary Pack diagnostics");
        check((await Analyze(text)).Items.Any(item => item.Severity == 1 && item.Message.Contains("Incorrect actual CMake defines", StringComparison.Ordinal)),
            "updated CMake target macro is reanalyzed in ordinary Pack");
        await File.WriteAllTextAsync(response, targetFlags, token);
        check((await Analyze(text)).Items.All(item => item.Severity != 1), "restoring target response automatically clears configuration errors");
        await File.WriteAllTextAsync(database, "invalid database", token);
        try
        {
            await service.RefreshEnvironmentAsync(token);
            throw new InvalidOperationException("Invalid CMake database silently used template fallback.");
        }
        catch (StudioXException error) when (error.Code == "LANGUAGE_DATABASE_INVALID")
        {
            check(!service.IsReady && service.GetDiagnostics().Count == 0, "malformed ordinary Pack database stops analysis and clears stale results instead of falling back");
        }
        await JsonStore.WriteAsync(database, entries, token);
        check((await Analyze(text)).Items.All(item => item.Severity != 1), "repairing ordinary Pack database automatically restores reliable analysis");
        await File.WriteAllTextAsync(Path.Combine(project, ".build/CMakeCache.txt"), "CMAKE_HOME_DIRECTORY:INTERNAL=C:/different-project\n", token);
        try
        {
            await service.RefreshEnvironmentAsync(token);
            throw new InvalidOperationException("Foreign project cache was accepted.");
        }
        catch (StudioXException error) when (error.Code == "LANGUAGE_CACHE_PROJECT")
        {
            check(!service.IsReady && service.GetDiagnostics().Count == 0, "copied ordinary Pack CMake cache gives an actionable project-identity error");
        }
        await service.StopAsync();
        File.Delete(Path.Combine(project, ".build/CMakeCache.txt"));
        var wchDevice = pack.Devices[0] with
        {
            Architecture = "riscv",
            ToolsetId = "wch.riscv",
            CompilerId = "wch-gcc-12.2.0-v1.4",
            CpuFlags = ["-march=rv32imac_xw", "-mabi=ilp32"]
        };
        await JsonStore.WriteAsync(packPath, pack with
        {
            Devices = [wchDevice]
        }, token);
        var manifestPath = Path.Combine(project, ".studiox/project.json");
        var manifest = await JsonStore.ReadAsync<ProjectManifest>(manifestPath, token);
        await JsonStore.WriteAsync(manifestPath, manifest with
        {
            ToolsetId = wchDevice.ToolsetId,
            CompilerId = wchDevice.CompilerId
        }, token);
        await JsonStore.WriteAsync(database, new[] { new
        {
            directory = Path.Combine(project, ".build"), file = "../" + path,
            command = "riscv-wch-elf-gcc -march=rv32imac_xw -mabi=ilp32 -DUSER_FEATURE=7 -I\"../Hardware 中文\" -c ../" + path
        } }, token);
        await service.StartAsync(project, token);
        // 只请求这一目标，不把前面的 Arm/C++ 夹具同步成 RISC-V 新文件。
        await service.SynchronizeDiagnosticsAsync(path, text, [new(path, text)], token);
        for (var attempt = 0; attempt < 150 && !service.GetDiagnostics().Any(batch => batch.Path == path); attempt++)
        {
            await Task.Delay(40, token);
        }
        check(service.GetDiagnostics().Any(batch => batch.Path == path && batch.IsComplete && batch.Items.All(item => item.Severity != 1)),
            "ordinary WCH Pack retains custom CMake includes and existing XW language compatibility");
        await service.StopAsync();
        await File.WriteAllLinesAsync(Path.Combine(output, "clangd.log"), service.DrainLog(), token);
    }
}
