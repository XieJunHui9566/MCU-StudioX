namespace StudioX.WorkspaceEditingValidation;

using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>实际 clangd 条件编译跟随已保存的 SDCC 模型；不模拟 ABI 或运行硬件。</summary>
internal static class SdccModelAnalysisChecks
{
    internal static async Task RunAsync(string runtime, string archive, string output)
    {
        if (Directory.Exists(output)) { throw new IOException("Use a new output directory."); }
        Directory.CreateDirectory(output);
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(archive);
        var project = Path.Combine(output, "project with spaces");
        await new ProjectService().CreateAsync(pack, "IAP15L2K61S2", "bare", "SdccModelAnalysis", project);
        var main = Path.Combine(project, "src/main.c");
        var original = await File.ReadAllBytesAsync(main);
        var builds = new BuildService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")));
        var checks = new List<string>();
        foreach (var selected in new[] { SdccMemoryModel.Small, SdccMemoryModel.Medium, SdccMemoryModel.Large, SdccMemoryModel.ProjectDefault })
        {
            await builds.SaveSettingsAsync(project, new(MemoryModel: selected));
            var expected = selected == SdccMemoryModel.ProjectDefault ? "LARGE" : selected.ToString().ToUpperInvariant();
            var text = "#include <stdint.h>\n#include \"stc15.h\"\n" +
                string.Join('\n', new[] { "SMALL", "MEDIUM", "LARGE" }.Select(model =>
                    (model == expected ? "#ifndef " : "#ifdef ") + "__SDCC_MODEL_" + model +
                    "\n#error Unexpected_memory_model_" + model + "\n#endif")) +
                "\nvolatile uint8_t probe;\nvoid main(void) { while (1) { ++probe; } }\n";
            var data = Path.Combine(output, selected.ToString());
            await using var service = new CodeIntelligenceService(runtime, data);
            try
            {
                await service.StartAsync(project);
                if (!service.IsReady) { throw new InvalidOperationException(service.StatusDescription); }
                await service.SynchronizeDiagnosticsAsync("src/main.c", text, [new("src/main.c", text)]);
                for (var attempt = 0; attempt < 100 && !service.GetDiagnostics().Any(batch => batch.Path == "src/main.c" && batch.Text == text); attempt++)
                {
                    await Task.Delay(100);
                }
                var batch = service.GetDiagnostics().SingleOrDefault(batch => batch.Path == "src/main.c" && batch.Text == text);
                await JsonStore.WriteAsync(Path.Combine(data, "diagnostics.json"), service.GetDiagnostics());
                if (batch is null || batch.Items.Any(item => item.Severity == 1))
                {
                    throw new InvalidOperationException("Model condition or standard integer analysis failed: " + selected);
                }
                if (!(await File.ReadAllBytesAsync(main)).AsSpan().SequenceEqual(original))
                {
                    throw new InvalidOperationException("Analysis changed the user's source.");
                }
                checks.Add(selected + ": real clangd accepts only __SDCC_MODEL_" + expected + ", stdint and an unchanged source");
                Console.WriteLine("PASS " + checks[^1]);
            }
            finally { await File.WriteAllLinesAsync(Path.Combine(data, "language.log"), service.DrainLog()); }
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { passed = true, checks, hardware = false });
    }
}
