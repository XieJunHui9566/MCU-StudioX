using System.Diagnostics;
using System.Text.Json;
using StudioX.Engine;
using StudioX.Foundation;

if (args is ["--self-check", var checkDirectory]) return await CacheChecks.RunAsync(checkDirectory);
if (args is ["--verify-runtime", var toolsetsRoot, var toolsetId, var toolsetVersion, var compilerId])
{
    var resolved = await new ToolsetCatalog(toolsetsRoot).ResolveAsync(toolsetId, toolsetVersion, compilerId,
        forceVerification: true, progress: new InlineProgress(Console.WriteLine));
    Console.WriteLine($"PASS: {resolved.Manifest.Id} {resolved.Manifest.Version}, {resolved.Manifest.Sha256.Count} indexed files verified.");
    return 0;
}
if (args is not ["--build-benchmark", var toolsets, var source, var destination]) return 2;
var root = Path.GetFullPath(destination);
if (Directory.Exists(root)) throw new InvalidOperationException("Use a new benchmark directory.");
var fixture = Path.Combine(root, "fixture");
foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
{
    var relative = Path.GetRelativePath(source, path).Replace('\\', '/');
    if (relative.StartsWith(".build/", StringComparison.Ordinal) || relative.StartsWith(".git/", StringComparison.Ordinal)) continue;
    var target = Path.Combine(fixture, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target);
}
var builder = new BuildService(new ToolsetCatalog(toolsets));
var measurements = new List<object>();
for (var run = 1; run <= 2; run++)
{
    var clock = Stopwatch.StartNew(); var phases = new List<object>();
    var progress = new InlineProgress(message =>
    {
        phases.Add(new { message, elapsedMs = clock.Elapsed.TotalMilliseconds });
        Console.WriteLine($"run {run}: {clock.Elapsed.TotalSeconds:F3}s {message}");
    });
    var result = await builder.BuildAsync(fixture, progress);
    clock.Stop();
    await File.WriteAllTextAsync(Path.Combine(root, $"build-{run}.log"), result.Log);
    if (!result.Success) throw new InvalidOperationException(result.Log);
    measurements.Add(new { run, elapsedMs = clock.Elapsed.TotalMilliseconds, result.Success, result.ExitCode, phases });
    Console.WriteLine($"run {run}: {clock.Elapsed.TotalSeconds:F3}s total");
}
await File.WriteAllTextAsync(Path.Combine(root, "timings.json"), JsonSerializer.Serialize(measurements, JsonStore.Options));
return 0;

sealed class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
