using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

if (args.Length is < 3 or > 4)
{
    Console.Error.WriteLine("Usage: StudioX.EspressifBuildValidation <runtime-directory> <projects-directory> <output-directory> [target]");
    return 2;
}
var runtime = Path.GetFullPath(args[0]);
var projects = Path.GetFullPath(args[1]);
var output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
var memoryOnly = args.Length == 4 && args[3].StartsWith("memory:", StringComparison.Ordinal);
var selected = args.Length == 4 ? (memoryOnly ? args[3][7..] : args[3]).Split(',') : ["esp32c3", "esp32s3", "esp32", "esp32p4", "esp32c5", "esp32c6"];
var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
if (memoryOnly) { return await EspressifMemoryChecks.RunAsync(catalog, projects, output, selected); }
if (args.Length == 4 && args[3] == "native-tools") { return await EspressifNativeToolChecks.RunAsync(catalog, projects, output); }
if (args.Length == 4 && args[3] == "boundaries") { return await EspressifBuildBoundaryChecks.RunAsync(catalog, projects, output); }
var builds = new BuildService(catalog);
var results = new List<object>();
var count = 0;
foreach (var target in selected)
{
    foreach (var template in new[] { "hello_world", "freertos" })
    {
        var name = target + "_" + template;
        var projectRoot = Path.Combine(projects, name);
        var cmake = await File.ReadAllBytesAsync(Path.Combine(projectRoot, "CMakeLists.txt"));
        var defaults = await File.ReadAllBytesAsync(Path.Combine(projectRoot, "sdkconfig.defaults"));
        var project = await ProjectService.ReadAsync(projectRoot);
        var expectedTarget = project.Espressif?.Target ?? throw new Exception("Missing SDK identity");
        Console.WriteLine("BUILD " + name);
        var report = await builds.BuildAsync(projectRoot, new ProgressText());
        await File.WriteAllTextAsync(Path.Combine(output, name + ".log"), report.Log);
        Check(report.Success, name + ": " + report.Log);
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(projectRoot, ".build/studiox-build-receipt.json")));
        var sourceStamp = receipt.RootElement.GetProperty("sourceStamp").GetString();
        Check(sourceStamp is { Length: 64 }, name + " includes final source/configuration stamp");
        var layout = receipt.RootElement.GetProperty("espressifLayout");
        Check(layout.GetProperty("target").GetString() == expectedTarget, name + " keeps exact target");
        var images = receipt.RootElement.GetProperty("images").EnumerateArray().ToArray();
        Check(images.Length >= 3 && images.Count(image => image.GetProperty("symbolsPath").ValueKind == JsonValueKind.String) == 1,
            name + " receipts bootloader/partition/application and one app ELF");
        foreach (var image in images)
        {
            var path = PathBoundary.Resolve(projectRoot, image.GetProperty("relativePath").GetString()!);
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            Check(hash == image.GetProperty("sha256").GetString(), name + " holds actual artifact hash");
        }
        Check((await File.ReadAllBytesAsync(Path.Combine(projectRoot, "CMakeLists.txt"))).AsSpan().SequenceEqual(cmake) &&
            (await File.ReadAllBytesAsync(Path.Combine(projectRoot, "sdkconfig.defaults"))).AsSpan().SequenceEqual(defaults),
            name + " keeps native user CMake and SDK defaults");
        var memory = await new BuildMemoryService(catalog).ReadAsync(projectRoot);
        await File.WriteAllTextAsync(Path.Combine(output, name + "-memory.json"), JsonSerializer.Serialize(memory, JsonStore.Options));
        Check(memory.Targets.Count == 1 && (memory.Targets[0].Regions.Count > 0 || !string.IsNullOrEmpty(memory.Targets[0].Diagnostic)),
            name + " exposes link regions or the raw alias diagnostic: " + JsonSerializer.Serialize(memory, JsonStore.Options));
        results.Add(new { name, report.Success, report.ExitCode, sourceStamp, imageCount = images.Length, memory.Message,
            memoryDiagnostic = memory.Targets[0].Diagnostic, memoryRegionCount = memory.Targets[0].Regions.Count });
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { count, results }, JsonStore.Options));
        Console.WriteLine("PASS " + name + " · " + images.Length + " flash images · " + memory.Message);
    }
}
Console.WriteLine("PASS " + count + " assertions, native builds completed without device access.");
return 0;

void Check(bool passed, string message)
{
    if (!passed)
    {
        throw new InvalidOperationException(message);
    }
    count++;
}

sealed class ProgressText : IProgress<string>
{
    public void Report(string value) => Console.WriteLine("  " + value);
}
