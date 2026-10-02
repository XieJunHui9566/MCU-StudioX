using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

internal static class CoreDumpChecks
{
    internal static async Task RunAsync(string root, string toolsets, string fixtures, Action<bool,string> check)
    {
        var catalog = new ToolsetCatalog(toolsets);
        var service = new FirmwareFaultService(catalog);
        foreach (var (target, suffix, hashed) in new[] { ("esp32", "", true), ("esp32s3", "_bin", false) })
        {
            var project = Path.Combine(root, target); Directory.CreateDirectory(Path.Combine(project, ".studiox"));
            await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), new ProjectManifest(1, target, "fixture", "1.0.0", new string('a',64),
                target == "esp32" ? "ESP32" : "ESP32-S3", "hello", "espressif.idf", "5.5.4", "esp-idf", Espressif: new("esp-idf", target, "5.5.4")));
            var dump = Path.Combine(fixtures, $"tests_{target}_coredump{suffix}.b64");
            var elf = Path.Combine(fixtures, $"tests_test_apps_built_apps_{target}{suffix}.elf");
            var report = await service.DecodeCoreDumpAsync(project, dump, "b64", elf);
            await JsonStore.WriteAsync(Path.Combine(root, target + "-report.json"), report);
            check(report.CoreDump is { } detail && detail.HashMatches == hashed && detail.Target == target && detail.DumpSha256 == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dump))), target + " official captured dump decoded with recorded hashes and truthful firmware status");
            check(report.CoreDump!.Tasks.Count > 0 && report.CoreDump.CrashedTask is not null && report.SymbolInformation.Contains("CURRENT THREAD STACK", StringComparison.OrdinalIgnoreCase) && report.SymbolInformation.Contains("#0", StringComparison.Ordinal), target + " real GDB decodes crashed task and call stack");
            if (hashed)
            {
                var wrong = Path.Combine(root, "wrong.elf"); File.Copy(elf, wrong); await File.AppendAllTextAsync(wrong, "changed");
                try { await service.DecodeCoreDumpAsync(project, dump, "b64", wrong); throw new InvalidOperationException("Expected mismatch rejection"); }
                catch (StudioXException e) when (e.Code == "FAULT_DUMP") { check(e.Message.Contains("SHA256", StringComparison.OrdinalIgnoreCase), "mismatched ELF rejected with original tool diagnostic"); }
                var raw = Path.Combine(root, "esp32.raw");
                // IDF 正文按行独立填充 Base64；按官方加载器的方式逐行拼回二进制。
                var decodedLines = (await File.ReadAllLinesAsync(dump)).Where(line => !string.IsNullOrWhiteSpace(line))
                    .SelectMany(line => Convert.FromBase64String(line.Trim())).ToArray();
                await File.WriteAllBytesAsync(raw, decodedLines);
                var rawReport = await service.DecodeCoreDumpAsync(project, raw, "raw", elf);
                check(rawReport.CoreDump is { Target: "esp32", HashMatches: true } && rawReport.CoreDump.Tasks.Count > 0,
                    "raw format decodes same official capture and verifies ELF hash");
                await JsonStore.WriteAsync(Path.Combine(root, "esp32-raw-report.json"), rawReport);
                var converter = Path.Combine(root, "convert-core.py");
                await File.WriteAllTextAsync(converter, """
import os, shutil, sys
from esp_coredump.corefile.loader import ESPCoreDumpFileLoader
loader = ESPCoreDumpFileLoader(sys.argv[1], False)
assert loader.target == 'esp32'
loader.create_corefile(exe_name=sys.argv[2])
shutil.copyfile(loader.core_elf_file, sys.argv[3])
os.unlink(loader.core_elf_file)
""");
                var core = Path.Combine(root, "esp32-core.elf");
                var resolved = (await catalog.ResolveAsync("espressif.idf", "5.5.4", "esp-idf")).ForEspressifTarget("esp32");
                var environment = ToolsetEnvironment.Create(resolved);
                environment["IDF_PATH"] = resolved.ResourceDirectory("idf");
                environment["PYTHONHOME"] = resolved.ResourceDirectory("python-env");
                environment["TEMP"] = root; environment["TMP"] = root;
                environment["PYTHONPYCACHEPREFIX"] = Path.Combine(root, "converter-cache");
                var conversion = await new ProcessRunner().RunAsync(new(resolved.Tool("python"), ["-I", "-B", converter, raw, elf, core], root,
                    TimeSpan.FromMinutes(1), environment, RemoveEnvironment: ToolsetEnvironment.AmbientVariables));
                await File.WriteAllTextAsync(Path.Combine(root, "core-conversion.log"), conversion.StandardOutput + conversion.StandardError);
                check(conversion.ExitCode == 0 && !conversion.TimedOut && !conversion.OutputTruncated && File.Exists(core),
                    "official loader converts captured raw data to core ELF offline");
                var coreReport = await service.DecodeCoreDumpAsync(project, core, "elf", elf);
                check(coreReport.CoreDump is { Target: "esp32", HashMatches: true } && coreReport.CoreDump.Tasks.Count > 0,
                    "direct core ELF validates target and embedded hash before GDB decoding");
                await JsonStore.WriteAsync(Path.Combine(root, "esp32-core-elf-report.json"), coreReport);
                try { await service.DecodeCoreDumpAsync(project, core, "elf", wrong); throw new InvalidOperationException("Expected direct core mismatch rejection"); }
                catch (StudioXException e) when (e.Code == "FAULT_DUMP") { check(e.Message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase), "direct core ELF with mismatched symbols rejected before GDB"); }
            }
            else
            {
                var changedManifest = await ProjectService.ReadAsync(project);
                await JsonStore.WriteAsync(Path.Combine(project, ".studiox/project.json"), changedManifest with { Espressif = new("esp-idf", "esp32", "5.5.4") });
                try { await service.DecodeCoreDumpAsync(project, dump, "b64", elf); throw new InvalidOperationException("Expected target rejection"); }
                catch (StudioXException e) when (e.Code == "FAULT_DUMP") { check(e.Message.Contains("target", StringComparison.OrdinalIgnoreCase), "mismatched target rejected before GDB and never probes serial ports"); }
            }
        }
    }
}
