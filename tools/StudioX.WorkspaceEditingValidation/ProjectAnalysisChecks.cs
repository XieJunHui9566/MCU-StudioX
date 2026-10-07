using StudioX.Application.CodeIntelligence;
using StudioX.Foundation;

/// <summary>只读验证实际工程；故意错误只送入语言缓冲区，不保存源码或执行构建。</summary>
internal static class ProjectAnalysisChecks
{
    public static async Task RunAsync(string runtime, string project, string output, Action<bool, string> check)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new project-analysis output directory.");
        }
        Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        const string path = "src/main.c";
        const string headerPath = "Hardware/BH1750.h";
        var text = await File.ReadAllTextAsync(Path.Combine(project, path), token);
        var header = await File.ReadAllTextAsync(Path.Combine(project, headerPath), token);
        var driver = await File.ReadAllTextAsync(Path.Combine(project, "Hardware/BH1750.c"), token);
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "language"));
        async Task<CodeDiagnosticBatch> Analyze(string source)
        {
            await service.SynchronizeDiagnosticsAsync(path, source,
                [new(path, source), new(headerPath, header), new("Hardware/BH1750.c", driver)], token);
            for (var attempt = 0; attempt < 150; attempt++)
            {
                if (service.GetDiagnostics().SingleOrDefault(batch => batch.Path == path && batch.Text == source) is { } batch)
                {
                    return batch;
                }
                await Task.Delay(40, token);
            }
            throw new InvalidOperationException("No fresh diagnostic batch for actual project.");
        }
        try
        {
            await service.StartAsync(project, token);
            var batch = await Analyze(text);
            await JsonStore.WriteAsync(Path.Combine(output, "diagnostics.json"), service.GetDiagnostics(), token);
            check(batch.IsComplete && batch.Items.All(item => item.Severity != 1), "actual LEDTEST main.c resolves BH1750.h without realtime errors");
            check(service.GetDiagnostics().All(item => item.IsComplete && item.Items.All(diagnostic => diagnostic.Severity != 1)),
                "actual sensor header and implementation have complete clean realtime diagnostics");
            var locations = await service.NavigateAsync(path, text, text.IndexOf("BH1750_Init", StringComparison.Ordinal), false, token);
            await JsonStore.WriteAsync(Path.Combine(output, "navigation.json"), locations, token);
            check(locations.Any(location => location.DocumentPath is headerPath or "Hardware/BH1750.c"), "actual BH1750 function declaration or definition is navigable");
            var broken = text.Replace("BH1750_Init()", "studiox_missing_sensor()", StringComparison.Ordinal);
            check((await Analyze(broken)).Items.Any(item => item.Severity == 1 && item.Message.Contains("studiox_missing_sensor", StringComparison.Ordinal)),
                "actual project still diagnoses a deliberately undefined function in an unsaved buffer");
            check((await Analyze(text)).Items.All(item => item.Severity != 1), "restoring actual text clears the deliberate error");
            check(await File.ReadAllTextAsync(Path.Combine(project, path), token) == text &&
                await File.ReadAllTextAsync(Path.Combine(project, headerPath), token) == header &&
                await File.ReadAllTextAsync(Path.Combine(project, "Hardware/BH1750.c"), token) == driver,
                "actual project source remains unchanged after verification");
        }
        finally
        {
            await service.StopAsync();
            var lines = service.DrainLog().ToArray();
            await File.WriteAllLinesAsync(Path.Combine(output, "clangd.log"), lines);
            if (lines.Any(line => line.Contains("IncludeCleaner: Failed", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("IncludeCleaner still reports unresolved headers; see clangd.log.");
            }
        }
    }
}
