using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

internal static class UsabilityChecks
{
    private sealed class Output(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }

    internal static async Task RunAsync(string tools, string project, string output)
    {
        Directory.CreateDirectory(output);
        var builder = new BuildService(new ToolsetCatalog(tools));
        var settings = new Mx251ProjectSettingsService(builder, new ProjectFileService());
        var initial = await settings.ReadAsync(project);
        var results = new List<object>();
        async Task Build(string label, string optimization, string standard, bool cached = false)
        {
            var current = await settings.ReadAsync(project);
            await settings.SaveAsync(project, current, current.Model, optimization, standard);
            var events = new List<object>();
            var lines = new List<string>();
            var clock = Stopwatch.StartNew();
            var progress = new Output(line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                    events.Add(new
                    {
                        seconds = clock.Elapsed.TotalSeconds,
                        line
                    });
                }
                if (line.Contains("[MX251]") || line.Contains("复用本次启动的构建产物"))

                {
                    Console.WriteLine($"{clock.Elapsed.TotalSeconds:F2}s {line}");
                }
            });
            var report = await builder.BuildAsync(project, progress, output: progress);
            await File.WriteAllTextAsync(Path.Combine(output, label + ".log"), report.Log);
            await JsonStore.WriteAsync(Path.Combine(output, label + "-events.json"), events);
            if (!report.Success)
            {
                throw new InvalidOperationException(report.Log);
            }
            var receipt = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(project, ".build/mx251/latest/receipt.json")))!;
            if (receipt["optimization"]!.GetValue<string>() != optimization || receipt["standard"]!.GetValue<string>() != standard)

            {
                throw new InvalidOperationException("Saved options did not reach compiler receipt");
            }
            var commands = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(project, ".build/mx251/latest/commands.json")))!.AsArray();
            var compile = commands.First(node => node!["arguments"]!.AsArray().Any(arg => arg!.GetValue<string>() == "-c"))!;
            var arguments = compile["arguments"]!.AsArray().Select(arg => arg!.GetValue<string>()).ToArray();
            if (!arguments.Contains("--opt-code-" + optimization) || !arguments.Contains("--std-" + standard))

            {
                throw new InvalidOperationException("Saved options did not reach compiler command");
            }
            if (cached ? !lines.Any(line => line.Contains("复用本次启动的构建产物")) :
                !lines.Any(line => line.Contains("[MX251] 编译源文件 [0/")))

            {
                throw new InvalidOperationException("Expected live progress or build reuse missing");
            }
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(project, ".build/firmware.hex"))));
            results.Add(new
            {
                label,
                seconds = clock.Elapsed.TotalSeconds,
                optimization,
                standard,
                hash,
                success = report.Success
            });
            await JsonStore.WriteAsync(Path.Combine(output, "results.json"), results);
            Console.WriteLine($"PASS {label} {clock.Elapsed.TotalSeconds:F3}s");
        }
        await Build("default", initial.Optimization, initial.Standard);
        await Build("size-c11", "size", "c11");
        await Build("unchanged", "size", "c11", cached: true);
        var final = await settings.ReadAsync(project);
        await settings.SaveAsync(project, final, initial.Model, initial.Optimization, initial.Standard);
    }
}
