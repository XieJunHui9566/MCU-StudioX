using System.Security.Cryptography;
using System.Text.Json.Nodes;
using StudioX.Engine;
using StudioX.Foundation;

internal static class QualifiedPublicationChecks
{
    private sealed class Output(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
    internal static async Task RunAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture.json")))!;
        var builder = new BuildService(new ToolsetCatalog(fixture["tools"]!.GetValue<string>()));
        var project = Path.Combine(root, "projects/ide/calls-small");
        var checks = new List<string>();
        void Check(bool ok, string label)
        {
            if (!ok)
            {
                throw new InvalidOperationException(label);
            }
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        var cmake = Path.Combine(project, "CMakeLists.txt");
        var original = await File.ReadAllTextAsync(cmake);
        var cfg = Path.Combine(project, "mx251.json");
        var originalConfig = await File.ReadAllTextAsync(cfg);
        await File.WriteAllTextAsync(cmake, original + "\nmessage(STATUS \"MX251_PUBLICATION_SOURCE_CHECK\")\n");
        var changed = false;
        try
        {
            await builder.BuildAsync(project, output: new Output(line =>
            {
                if (!changed && line.Contains("MX251_PUBLICATION_SOURCE_CHECK"))
                {
                    changed = true;
                    File.WriteAllText(cfg, originalConfig + "\n");
                }
            }));
            throw new InvalidOperationException("Source changed but firmware published");
        }
        catch (StudioXException e) when (e.Code == "MX251_SOURCE_CHANGED") { Check(changed, "source/config change during build rejects publication"); }
        foreach (var p in new[] { ".build/firmware.hex", ".build/firmware.bin", ".build/studiox-build-receipt.json", ".build/mx251/latest/receipt.json" })
        {
            Check(!File.Exists(Path.Combine(project, p)), "source change cleared " + p);
        }
        await File.WriteAllTextAsync(cmake, original);
        await File.WriteAllTextAsync(cfg, originalConfig);
        var report = await builder.BuildAsync(project);
        await File.WriteAllTextAsync(Path.Combine(root, "publication-final-build.log"), report.Log);
        Check(report.Success && report.Artifacts.Any(p => p.EndsWith("firmware.bin")), "final build publishes both artifacts");
        var expected = fixture["cases"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "calls-small")!["off"]!.GetValue<string>();
        Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(project, ".build/firmware.hex")))).Equals(expected, StringComparison.OrdinalIgnoreCase), "final implementation exact sealed HEX");
        Check((await new BuildMemoryService().ReadAsync(project)).Targets.Count > 0, "final memory analysis available");
        await JsonStore.WriteAsync(Path.Combine(root, "publication-results.json"), new
        {
            checks,
            hardware = false,
            complete = true
        });
    }
}
