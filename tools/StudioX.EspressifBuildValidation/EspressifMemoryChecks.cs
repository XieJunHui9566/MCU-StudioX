using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

internal static class EspressifMemoryChecks
{
    internal static async Task<int> RunAsync(ToolsetCatalog catalog, string projects, string output, string[] targets)
    {
        var service = new BuildMemoryService(catalog);
        var results = new List<object>();
        foreach (var target in targets)
        {
            foreach (var template in new[] { "hello_world", "freertos" })
            {
                var name = target + "_" + template;
                var root = Path.Combine(projects, name);
                var report = await service.ReadAsync(root);
                if (report.Targets.Count != 1 || report.Targets[0].Diagnostic is not null ||
                    report.Targets[0].Regions.Any(region => !region.IsLogical) ||
                    !report.Targets[0].Regions.Any(region => region.Name.Contains("RAM", StringComparison.Ordinal) && region.Capacity > 0) ||
                    !report.Targets[0].Regions.Any(region => region.Name.StartsWith("Flash", StringComparison.Ordinal) && region.Capacity == 0 && region.Used > 0))
                {
                    throw new InvalidOperationException(name + ": " + JsonSerializer.Serialize(report, JsonStore.Options));
                }
                var logPath = Path.Combine(root, ".build/studiox-idf-size.log");
                var modified = File.GetLastWriteTimeUtc(logPath);
                var cached = await service.ReadAsync(root);
                if (File.GetLastWriteTimeUtc(logPath) != modified || JsonSerializer.Serialize(cached, JsonStore.Options) != JsonSerializer.Serialize(report, JsonStore.Options))
                {
                    throw new InvalidOperationException(name + " SDK statistics cache did not reuse the unchanged native result");
                }
                results.Add(new
                {
                    name,
                    report,
                    cacheReused = true
                });
                await File.WriteAllTextAsync(Path.Combine(output, name + "-memory.json"), JsonSerializer.Serialize(report, JsonStore.Options));
                Console.WriteLine("PASS native size " + name + " · " + string.Join(", ", report.Targets[0].Regions.Select(region => region.Name + "=" + region.Used)));
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            count = results.Count,
            results
        }, JsonStore.Options));
        return 0;
    }
}
