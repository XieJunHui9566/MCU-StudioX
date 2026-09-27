namespace StudioX.EspressifValidation;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;

internal static class EspressifLanguageChecks
{
    public static async Task RunFallbackAsync(string runtime, string project, string output)
    {
        if (File.Exists(Path.Combine(project, ".build", "compile_commands.json")) || Directory.Exists(output))
        {
            throw new IOException("Fallback validation requires an unconfigured project and new output directory.");
        }
        Directory.CreateDirectory(output);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var manifest = await ProjectService.ReadAsync(project, cancellation.Token);
        await using var service = new CodeIntelligenceService(runtime, output);
        await service.StartAsync(project, cancellation.Token);
        const string text = "struct Local { int member; }; void example(void) { struct Local value; value.mem }\n";
        var result = await service.CompleteAsync(manifest.EntryFile ?? "src/main.c", text,
            text.IndexOf("value.mem", StringComparison.Ordinal) + "value.mem".Length, cancellation.Token);
        if (!service.IsReady || !service.StatusDescription.Contains("请先配置或编译", StringComparison.Ordinal) ||
            !result.Any(item => item.InsertText.Contains("member", StringComparison.Ordinal)) ||
            File.Exists(Path.Combine(project, ".build", "compile_commands.json")))
        {
            throw new InvalidOperationException("Unconfigured SDK project must provide generic editing without claiming SDK indexing or starting a build.");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "fallback-result.txt"),
            "PASS actual clangd generic completion before configuration; SDK-index limitation visible; no project build started.\n", cancellation.Token);
        Console.WriteLine("PASS unconfigured SDK project: generic completion, explicit SDK-index limitation, no implicit build.");
    }

    public static async Task RunAsync(string runtime, string projects, string output)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new language validation directory.");
        }
        Directory.CreateDirectory(output);
        var checkedTargets = new HashSet<string>(StringComparer.Ordinal);
        string? aliasSource = null;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        foreach (var directory in Directory.EnumerateDirectories(projects))
        {
            if (!File.Exists(Path.Combine(directory, ".studiox", "project.json")) ||
                !File.Exists(Path.Combine(directory, ".build", "compile_commands.json")))
            {
                continue;
            }
            var project = await ProjectService.ReadAsync(directory, cancellation.Token);
            if (project.Espressif?.Target is not ("esp32c3" or "esp32s3") || !checkedTargets.Add(project.Espressif.Target))
            {
                continue;
            }
            var nativeDatabase = Path.Combine(directory, ".build", "compile_commands.json");
            var entryFile = project.EntryFile ?? "src/main.c";
            var entryPath = Path.Combine(directory, entryFile);
            if (project.Espressif.Target == "esp32c3")
            {
                aliasSource = directory;
            }
            var hashBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(nativeDatabase, cancellation.Token)));
            var cacheRoot = Path.Combine(output, project.Espressif.Target);
            await using var service = new CodeIntelligenceService(runtime, cacheRoot);
            await service.StartAsync(directory, cancellation.Token);
            var prefix = "#include \"esp_system.h\"\n#include \"freertos/FreeRTOS.h\"\n#include \"freertos/task.h\"\n";
            foreach (var (partial, expected) in new[] { ("esp_get_free_heap_si", "esp_get_free_heap_size"), ("vTaskDel", "vTaskDelay") })
            {
                var text = prefix + "void example(void) { " + partial + "\n}\n";
                var result = await service.CompleteAsync(entryFile, text, text.IndexOf(partial, StringComparison.Ordinal) + partial.Length, cancellation.Token);
                if (!result.Any(item => item.InsertText.Contains(expected, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(project.DeviceId + ": SDK completion missing " + expected + "\n" + string.Join('\n', service.DrainLog()));
                }
            }
            var navigationText = prefix + "void example(void) { (void)esp_get_free_heap_size(); }\n";
            var locations = await service.NavigateAsync(entryFile, navigationText,
                navigationText.IndexOf("esp_get_free_heap_size", StringComparison.Ordinal) + 3, true, cancellation.Token);
            if (locations.Count == 0 || !(await service.ReadNavigationDocumentAsync(locations[0], cancellation.Token)).IsReadOnly)
            {
                throw new InvalidOperationException(project.DeviceId + ": SDK declaration must navigate into a readable, readonly header.");
            }
            var analysisFile = Directory.GetFiles(cacheRoot, "compile_commands.json", SearchOption.AllDirectories).Single();
            using var analysisJson = JsonDocument.Parse(await File.ReadAllBytesAsync(analysisFile, cancellation.Token));
            var commands = analysisJson.RootElement.EnumerateArray().ToArray();
            var entry = commands.Single(command => EspressifPathIdentity.NormalizePath(command.GetProperty("file").GetString()!) ==
                EspressifPathIdentity.NormalizePath(entryPath));
            var arguments = entry.GetProperty("arguments").EnumerateArray().Select(argument => argument.GetString()!).ToArray();
            if (!arguments.Any(argument => argument.StartsWith("-D", StringComparison.Ordinal)) ||
                !arguments.Any(argument => argument.StartsWith("-I", StringComparison.Ordinal)) ||
                !arguments.Any(argument => argument.StartsWith("-std=", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(project.DeviceId + ": source macros, includes and language standard must come from the native database.");
            }
            if (project.Espressif.Target == "esp32s3" && (!arguments.Contains("--target=i386-unknown-elf") ||
                arguments.Any(argument => argument == "-mlongcalls") || !service.StatusDescription.Contains("ABI", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Xtensa fallback must state its ABI limitation and omit unsupported machine flags.");
            }
            if (project.Espressif.Target == "esp32c3" && !arguments.Contains("--target=riscv32-unknown-elf"))
            {
                throw new InvalidOperationException("RISC-V source analysis must retain the correct target architecture.");
            }
            var cppText = "#include <stdint.h>\nclass Test { public: int member; }; void hint() { Test value; value.mem }\n";
            var cppFile = Path.Combine(Path.GetDirectoryName(entryFile)!, "new_ui.cpp").Replace('\\', '/');
            var cppResult = await service.CompleteAsync(cppFile, cppText,
                cppText.IndexOf("value.mem", StringComparison.Ordinal) + "value.mem".Length, cancellation.Token);
            if (!cppResult.Any(item => item.InsertText.Contains("member", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(project.DeviceId + ": newly opened C++ source cannot inherit a C language standard.");
            }
            var hashAfter = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(nativeDatabase, cancellation.Token)));
            if (hashBefore != hashAfter)
            {
                throw new InvalidOperationException("Language analysis changed the native compile database.");
            }
            await File.WriteAllLinesAsync(Path.Combine(cacheRoot, "log.txt"), service.DrainLog(), cancellation.Token);
            Console.WriteLine("PASS " + project.DeviceId + ": SDK/FreeRTOS completion, readonly SDK navigation, C++ member completion, architecture scope and unchanged native database.");
        }
        if (!checkedTargets.SetEquals(["esp32c3", "esp32s3"]))
        {
            throw new InvalidOperationException("Need configured ESP32-C3 and ESP32-S3 projects.");
        }
        await EspressifLanguageAliasChecks.RunAsync(runtime, aliasSource!, output, cancellation.Token);
        await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), "PASS ESP32-C3 and ESP32-S3 actual clangd SDK navigation and completion; native databases unchanged. No hardware access.\n", cancellation.Token);
    }
}
