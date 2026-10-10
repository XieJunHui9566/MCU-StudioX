using System.Diagnostics;
using System.Text.Json;
using StudioX.Foundation;
using StudioX.Packages;

internal static class CatalogMeasurements
{
    public static async Task RunAsync(string packRoot, string bundleRoot, string output)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output))
        {
            throw new ArgumentException("Use a new measurement output directory.");
        }
        Directory.CreateDirectory(output);
        var repository = new PackRepository(packRoot);
        var watch = Stopwatch.StartNew();
        var catalog = await repository.ListCatalogAsync();
        var firstMilliseconds = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        _ = await repository.ListCatalogAsync();
        var repeatMilliseconds = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var retained = PackCatalogPolicy.SelectCurrentVersions(catalog);
        var policyMilliseconds = watch.Elapsed.TotalMilliseconds;
        using var bundled = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(bundleRoot, "index.json")));
        var known = catalog.Select(pack => (pack.Manifest.Id, pack.Manifest.Version)).ToHashSet();
        var missing = bundled.RootElement.EnumerateArray().Where(entry => !known.Contains((entry.GetProperty("id").GetString()!, entry.GetProperty("version").GetString()!))).ToArray();
        var missingArchiveBytes = missing.Sum(entry => new FileInfo(PathBoundary.Resolve(bundleRoot, entry.GetProperty("file").GetString()!)).Length);
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            packRoot = Path.GetFullPath(packRoot),
            packs = catalog.Count,
            firstMilliseconds,
            repeatMilliseconds,
            policyMilliseconds,
            supersededVersions = catalog.Count - retained.Count,
            missingBundledIdentities = missing.Length,
            missingArchiveBytes,
            readOnly = true,
            hardware = false
        });
        Console.WriteLine(await File.ReadAllTextAsync(Path.Combine(output, "result.json")));
    }
}
