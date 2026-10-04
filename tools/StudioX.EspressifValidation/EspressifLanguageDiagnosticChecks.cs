namespace StudioX.EspressifValidation;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;

internal static class EspressifLanguageDiagnosticChecks
{
    public static async Task RunAsync(string runtime, string projects, string output)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new diagnostics validation directory.");
        }
        Directory.CreateDirectory(output);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var results = new List<object>();
        var failures = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(projects))
        {
            var database = Path.Combine(directory, ".build", "compile_commands.json");
            if (!File.Exists(database) || !File.Exists(Path.Combine(directory, ".studiox", "project.json")))
            {
                continue;
            }
            var project = await ProjectService.ReadAsync(directory, cancellation.Token);
            if (project.Espressif is null)
            {
                continue;
            }
            var target = project.Espressif.Target;
            var caseName = target + "-" + Path.GetFileName(directory);
            var entry = project.EntryFile ?? "main/hello_world_main.c";
            var hashBefore = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(database, cancellation.Token)));
            await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, caseName));
            await service.StartAsync(directory, cancellation.Token);
            // 使用编辑缓冲区复现截图，不改用户工程，也不启动固件或硬件操作。
            const string valid = "#include <stdio.h>\n#include <stdlib.h>\n#include <errno.h>\n#include <string.h>\n#include \"esp_system.h\"\n" +
                "#include \"freertos/FreeRTOS.h\"\n#include \"freertos/task.h\"\n" +
                "void diagnostic_probe(void) { FILE *streams[] = { stdin, stdout, stderr }; fprintf(streams[2], \"%d\\n\", errno); " +
                "fflush(stdout); (void)strlen(\"ok\"); (void)malloc(1); esp_restart(); }\n";
            var validBatch = await DiagnoseAsync(service, entry, valid, cancellation.Token);
            var broken = valid.Replace("fflush(stdout);", "fflush(studiox_missing_stdout);", StringComparison.Ordinal);
            var brokenBatch = await DiagnoseAsync(service, entry, broken, cancellation.Token);
            var fixedBatch = await DiagnoseAsync(service, entry, valid, cancellation.Token);
            var errors = validBatch.Items.Where(item => item.Severity == 1).ToArray();
            var realError = brokenBatch.Items.Any(item => item.Severity == 1 && item.Message.Contains("studiox_missing_stdout", StringComparison.Ordinal));
            var cleared = fixedBatch.Items.All(item => item.Severity != 1);
            var unchanged = hashBefore == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(database, cancellation.Token)));
            var success = errors.Length == 0 && realError && cleared && unchanged;
            if (!success)
            {
                failures.Add(project.Espressif.SdkVersion + "/" + target);
            }
            results.Add(new
            {
                project = directory,
                sdkVersion = project.Espressif.SdkVersion,
                target,
                success,
                diagnostics = errors,
                deliberatelyBrokenDiagnostics = brokenBatch.Items,
                correctedDiagnostics = fixedBatch.Items,
                realErrorRetained = realError,
                correctedTextClearsErrors = cleared,
                nativeDatabaseUnchanged = unchanged,
                hardware = false
            });
            await File.WriteAllLinesAsync(Path.Combine(output, caseName + ".log"), service.DrainLog(), cancellation.Token);
            Console.WriteLine((success ? "PASS " : "FAIL ") + project.Espressif.SdkVersion + "/" + target + ": " + string.Join("; ", errors.Select(item => item.Message)));
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                results,
                failures,
                hardware = false
            }, new JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
        }
        if (results.Count == 0)
        {
            throw new InvalidOperationException("Need configured native ESP projects.");
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Live diagnostics checks failed: " + string.Join(", ", failures));
        }
    }

    private static async Task<CodeDiagnosticBatch> DiagnoseAsync(CodeIntelligenceService service, string entry, string text, CancellationToken token)
    {
        await service.SynchronizeDiagnosticsAsync(entry, text, [new(entry, text)], token);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var batch = service.GetDiagnostics().SingleOrDefault(item => item.Path == entry && item.Text == text);
            if (batch is not null)
            {
                return batch;
            }
            await Task.Delay(100, token);
        }
        throw new InvalidOperationException("clangd did not publish diagnostics for the current document snapshot.\n" + string.Join('\n', service.DrainLog()));
    }
}
