using StudioX.Application.CodeIntelligence;
using StudioX.Foundation;

internal static class KeilCompilationChecks
{
    public static async Task RunAsync(string runtime, string templateProject, string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        // 单独的移植夹具不能改变后续界面验收使用的普通工程配置。
        var fixture = Path.Combine(output, "project");
        foreach (var relative in new[] { ".studiox/project.json", "device/manifest.json" })
        {
            var target = Path.Combine(fixture, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(templateProject, relative), target);
        }
        Directory.CreateDirectory(Path.Combine(fixture, "src"));
        Directory.CreateDirectory(Path.Combine(fixture, "include"));
        Directory.CreateDirectory(Path.Combine(fixture, ".build"));
        Directory.CreateDirectory(Path.Combine(fixture, "User"));
        Directory.CreateDirectory(Path.Combine(fixture, "Original Inc"));
        const string path = "User/keil.c";
        const string headerPath = "Original Inc/driver.h";
        const string header = "#if KEIL_DRIVER != 7\n#error Incorrect original target defines\n#endif\nstruct Driver { int value; };\nint original_driver(struct Driver *driver);\n";
        const string source = "#include \"driver.h\"\nint use_driver(struct Driver *driver) { return original_driver(driver); }\n";
        await File.WriteAllTextAsync(Path.Combine(fixture, path), source);
        await File.WriteAllTextAsync(Path.Combine(fixture, headerPath), header);
        await File.WriteAllTextAsync(Path.Combine(fixture, ".studiox/keil-import.json"), "{}");
        var database = Path.Combine(fixture, ".build/compile_commands.json");
        var response = Path.Combine(fixture, ".build/target.rsp");
        await File.WriteAllTextAsync(response, "-mcpu=cortex-m3 -mthumb -DKEIL_DRIVER=7 -I\"../Original Inc\" -std=gnu11");
        await JsonStore.WriteAsync(database, new[] { new
        {
            directory = Path.Combine(fixture, ".build"), file = "../" + path,
            command = "arm-none-eabi-gcc @target.rsp -c ../User/keil.c -o keil.o"
        }});
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "language"));
        await service.StartAsync(fixture, token);
        async Task<CodeDiagnosticBatch> Diagnose(string text)
        {
            await service.SynchronizeDiagnosticsAsync(path, text, [new(path, text), new(headerPath, header)], token);
            for (var attempt = 0; attempt < 150; attempt++)
            {
                if (service.GetDiagnostics().FirstOrDefault(b => b.Path == path && b.Text == text) is { } batch)
                {
                    return batch;
                }
                await Task.Delay(40, token);
            }
            throw new InvalidOperationException("No CMake diagnostic batch: " + string.Join('\n', service.DrainLog()));
        }
        check((await Diagnose(source)).Items.All(d => d.Severity != 1), "Keil Pack uses actual CMake include paths with spaces, response files and target defines");
        var definition = await service.NavigateAsync(path, source, source.IndexOf("original_driver", StringComparison.Ordinal), false, token, [new(path, source), new(headerPath, header)]);
        check(definition.Any(d => d.DocumentPath == headerPath), "Keil original header declarations remain navigable");
        var broken = source.Replace("original_driver(driver)", "missing_driver(driver)", StringComparison.Ordinal);
        check((await Diagnose(broken)).Items.Any(d => d.Severity == 1 && d.Message.Contains("missing_driver", StringComparison.Ordinal)), "CMake analysis retains actual undefined symbol errors");
        await File.WriteAllTextAsync(response, "-mcpu=cortex-m3 -mthumb -DKEIL_DRIVER=8 -I\"../Original Inc\" -std=gnu11", token);
        await service.RefreshEnvironmentAsync(token);
        check((await Diagnose(source)).Items.Any(d => d.Severity == 1 && d.Message.Contains("Incorrect original target defines", StringComparison.Ordinal)), "changed CMake response file refreshes target configuration");
        await service.StopAsync();
        File.Delete(database);
        try
        {
            await service.StartAsync(fixture, token);
            throw new InvalidOperationException("Missing migration database was silently accepted");
        }
        catch (StudioXException e) when (e.Code == "LANGUAGE_DATABASE_MISSING")
        {
            check(true, "missing Keil compile database gives actionable configure/build prompt");
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "clangd.log"), service.DrainLog(), token);
    }
}
