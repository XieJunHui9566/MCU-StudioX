namespace StudioX.EspressifValidation;

using System.Text.Json.Nodes;
using System.Text.Json;
using StudioX.Application.CodeIntelligence;
using StudioX.Foundation;
using StudioX.Engine;

internal static class EnvironmentReloadChecks
{
    internal static async Task RunAsync(string runtime, string root, string output, Action<bool, string> check, CancellationToken token)
    {
        var project = await ProjectService.ReadAsync(root, token);
        var entry = project.EntryFile!;
        var database = Path.Combine(root, ".build/compile_commands.json");
        var originalDatabase = await File.ReadAllBytesAsync(database, token);
        var source = await File.ReadAllBytesAsync(Path.Combine(root, entry), token);
        var configPath = Path.Combine(root, ".build/config/sdkconfig.json");
        var originalConfig = await File.ReadAllBytesAsync(configPath, token);
        var responsePath = Path.Combine(root, ".build/toolchain/cflags");
        var originalResponse = File.Exists(responsePath) ? await File.ReadAllBytesAsync(responsePath, token) : null;
        const string text = "#include <stdio.h>\n#ifndef STUDIOX_REFRESH_VALUE\n#error configuration_refresh_required\n#endif\nvoid app_main(void) { fflush(stdout); }\n";
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "language-data"));
        try
        {
            await service.StartAsync(root, token);
            var first = await DiagnoseAsync(service, entry, text, token, batch => batch.Items.Any(item => item.Severity == 1 && item.Message.Contains("configuration_refresh_required", StringComparison.OrdinalIgnoreCase)));
            await File.WriteAllTextAsync(Path.Combine(output, "reload-before-diagnostics.json"), JsonSerializer.Serialize(first, JsonStore.Options), token);
            check(first.Items.Any(item => item.Severity == 1 && item.Message.Contains("configuration_refresh_required", StringComparison.OrdinalIgnoreCase)), "real clangd publishes the deliberately missing macro before configuration refresh");
            var commands = JsonNode.Parse(originalDatabase)!.AsArray();
            var command = commands.Single(value => Path.GetFileName(value!["file"]!.GetValue<string>()) == Path.GetFileName(entry))!;
            if (command["arguments"] is JsonArray arguments)
            {
                arguments.Add("-DSTUDIOX_REFRESH_VALUE=1");
            }
            else
            {
                command["command"] = command["command"]!.GetValue<string>() + " -DSTUDIOX_REFRESH_VALUE=1";
            }
            await File.WriteAllTextAsync(database, commands.ToJsonString(), token);
            var refreshed = await DiagnoseAsync(service, entry, text, token);
            check(refreshed.Items.All(item => item.Severity != 1), "native database changes automatically reload analysis without reopening the project");
            var sourceAfter = await File.ReadAllBytesAsync(Path.Combine(root, entry), token);
            check(source.SequenceEqual(sourceAfter), "configuration reload preserves the unsaved editor buffer and disk source");
            check(!await service.RefreshEnvironmentAsync(token), "unchanged inputs do not restart the language server");
            if (originalResponse is not null)
            {
                var responseText = text.Replace("void app_main", "#ifndef STUDIOX_RESPONSE_REFRESH\n#error response_refresh_required\n#endif\nvoid app_main", StringComparison.Ordinal);
                await DiagnoseAsync(service, entry, responseText, token, batch => batch.Items.Any(item => item.Message.Contains("response_refresh_required", StringComparison.OrdinalIgnoreCase)));
                await File.WriteAllTextAsync(responsePath, System.Text.Encoding.UTF8.GetString(originalResponse) + "\n-DSTUDIOX_RESPONSE_REFRESH=1\n", token);
                check((await DiagnoseAsync(service, entry, responseText, token)).Items.All(item => item.Severity != 1), "response-file-only changes refresh the actual compiler macros automatically");
                if (project.Espressif?.Target == "esp32c3")
                {
                    var architecture = text + "\n#if !defined(__riscv_mul) || !defined(__riscv_compressed) || defined(__riscv_atomic)\n#error native_isa_response_missing\n#endif\n";
                    check((await DiagnoseAsync(service, entry, architecture, token)).Items.All(item => item.Severity != 1), "C3 analysis uses the native rv32imc ISA from response arguments");
                }
                await File.WriteAllTextAsync(responsePath, "@\"" + responsePath.Replace('\\', '/') + "\"\n", token);
                try
                {
                    await service.RefreshEnvironmentAsync(token);
                    throw new InvalidOperationException("Expected response cycle rejection.");
                }
                catch (StudioXException error) when (error.Code == "LANGUAGE_ESPRESSIF_RESPONSE") { check(true, "recursive response inputs stop analysis with a specific bounded-read diagnostic"); }
                await File.WriteAllBytesAsync(responsePath, originalResponse, token);
                check(await service.RefreshEnvironmentAsync(token), "repairing response arguments restores language analysis");
            }
            var generated = JsonNode.Parse(originalConfig)!;
            generated["IDF_TARGET"] = "esp32s3";
            await File.WriteAllTextAsync(configPath, generated.ToJsonString(), token);
            try
            {
                await service.RefreshEnvironmentAsync(token);
                throw new InvalidOperationException("Expected generated target rejection.");
            }
            catch (StudioXException error) when (error.Code == "LANGUAGE_CACHE_TARGET") { check(true, "generated target conflict is reported explicitly"); }
            check(!service.IsReady && service.GetDiagnostics().Count == 0, "failed environment refresh withdraws old diagnostics and stops using the stale server");
            check(!await service.RefreshEnvironmentAsync(token), "unchanged invalid inputs do not repeatedly restart or flood diagnostics");
            await File.WriteAllBytesAsync(configPath, originalConfig, token);
            check(await service.RefreshEnvironmentAsync(token), "repairing configuration restores analysis without reopening the IDE");
            var repaired = await DiagnoseAsync(service, entry, text, token);
            check(repaired.Items.All(item => item.Severity != 1), "repaired generation analyzes the same retained buffer correctly");
            var broken = text.Replace("fflush(stdout)", "fflush(studiox_real_error)", StringComparison.Ordinal);
            var bad = await DiagnoseAsync(service, entry, broken, token);
            check(bad.Items.Any(item => item.Severity == 1 && item.Message.Contains("studiox_real_error")), "genuine C errors survive automatic environment refresh");
            check((await DiagnoseAsync(service, entry, text, token)).Items.All(item => item.Severity != 1), "correcting the retained buffer clears the genuine error");
        }
        finally
        {
            await File.WriteAllBytesAsync(database, originalDatabase, CancellationToken.None);
            await File.WriteAllBytesAsync(configPath, originalConfig, CancellationToken.None);
            if (originalResponse is not null)
            {
                await File.WriteAllBytesAsync(responsePath, originalResponse, CancellationToken.None);
            }
            await File.WriteAllLinesAsync(Path.Combine(output, "environment-reload.log"), service.DrainLog(), CancellationToken.None);
        }
    }
    private static async Task<CodeDiagnosticBatch> DiagnoseAsync(CodeIntelligenceService service, string entry, string text, CancellationToken token,
        Func<CodeDiagnosticBatch, bool>? expected = null)
    {
        await service.SynchronizeDiagnosticsAsync(entry, text, [new(entry, text)], token);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var batch = service.GetDiagnostics().SingleOrDefault(item => item.Path == entry && item.Text == text);
            if (batch is not null && (expected?.Invoke(batch) ?? true))
            {
                return batch;
            }
            await Task.Delay(100, token);
        }
        throw new IOException("No expected current-generation clangd diagnostics.\n" + JsonSerializer.Serialize(service.GetDiagnostics(), JsonStore.Options) + "\n" + string.Join('\n', service.DrainLog()));
    }
}
