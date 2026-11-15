using System.Diagnostics;
using System.Security.Cryptography;
using StudioX.Application.Output;
using StudioX.Engine;
using StudioX.Foundation;

internal static class BuildPerformanceChecks
{
    private sealed class Progress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    internal static async Task RunAsync(string tools, string project, string output)
    {
        Directory.CreateDirectory(output);
        var catalog = new ToolsetCatalog(tools);
        var builder = new BuildService(catalog);
        var results = new List<object>();
        void Check(bool value, string message)
        {
            if (!value)
            {
                throw new InvalidOperationException(message);
            }
            Console.WriteLine("PASS " + message);
        }
        async Task<(BuildReport Report, List<string> Events, double Seconds)> Build(string label)
        {
            var events = new List<string>();
            var clock = Stopwatch.StartNew();
            var report = await builder.BuildAsync(project, new Progress(line =>
            {
                events.Add(line);
                Console.WriteLine(line);
            }),
                output: new Progress(line =>
                {
                    events.Add(line);
                    if (line.Contains("[MX251]", StringComparison.Ordinal))
                    {
                        Console.WriteLine($"{clock.Elapsed.TotalSeconds:F2}s {line.Trim()}");
                    }
                }));
            var seconds = clock.Elapsed.TotalSeconds;
            await File.WriteAllTextAsync(Path.Combine(output, label + ".log"), report.Log);
            results.Add(new
            {
                label,
                seconds,
                report.Success,
                events
            });
            await JsonStore.WriteAsync(Path.Combine(output, "results.json"), results);
            return (report, events, seconds);
        }
        var first = await Build("first");
        Check(first.Report.Success, "first full build");
        var hex = Path.Combine(project, ".build/firmware.hex");
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(hex));
        Check(first.Events.Any(e => e.Contains("[MX251] 编译源文件 [0/")), "live source progress received before completion");
        Check(first.Events.Any(e => e.Contains("复用本次启动的工具校验")), "SDK reuses verified component contents");
        var repeat = await Build("unchanged");
        Check(repeat.Report.Success && repeat.Events.Any(e => e.Contains("复用本次启动的构建产物")), "unchanged project skips compiler");
        Check(!repeat.Events.Any(e => e.StartsWith("完整校验", StringComparison.Ordinal)), "unchanged component skips full verification");
        var repeatedHash = SHA256.HashData(await File.ReadAllBytesAsync(hex));
        Check(hash.SequenceEqual(repeatedHash), "unchanged HEX is byte-identical");

        // 只改头文件；缓存必须失效，真实编译器必须看到错误，旧成功固件必须撤下。
        var header = Path.Combine(project, "include/mx251_stc_compat.h");
        var original = await File.ReadAllTextAsync(header);
        try
        {
            await File.AppendAllTextAsync(header, "\n#error MX251_CACHE_HEADER_CHANGED\n");
            var changed = await Build("header-change");
            Check(!changed.Report.Success && changed.Report.Log.Contains("MX251_CACHE_HEADER_CHANGED"), "header edit reaches compiler and invalidates build cache");
            Check(!File.Exists(hex) && !File.Exists(Path.Combine(project, ".build/mx251/latest/receipt.json")), "failure removes stale firmware and receipt");
        }
        finally
        {
            await File.WriteAllTextAsync(header, original);
        }

        Check(BuildOutputParser.Stage("[MX251] 编译源文件 [1/2] src/main.c") == "MX251 · 编译源文件" &&
            BuildOutputParser.Measure("[MX251] 编译源文件 [1/2] src/main.c")?.Percent == 50 &&
            BuildOutputParser.Measure("[MX251] 链接固件") is null, "phase progress uses real counts; linking has no fabricated percentage");

        // 更改已索引工具文件时间，公共缓存必须重新校验；不改工具内容。
        var tool = Path.Combine(tools, "studiox.mx251/0.0.170/sdk/build/qualified/VerifiedTools.ps1");
        var stamp = File.GetLastWriteTimeUtc(tool);
        try
        {
            File.SetLastWriteTimeUtc(tool, stamp.AddSeconds(2));
            var events = new List<string>();
            await catalog.ResolveAsync("studiox.mx251", "0.0.170", "mx251-v0.0170", progress: new Progress(events.Add));
            Check(events.Any(e => e.StartsWith("完整校验", StringComparison.Ordinal)), "tool metadata change invalidates verification cache");
        }
        finally
        {
            File.SetLastWriteTimeUtc(tool, stamp);
        }
        Console.WriteLine($"TIMING first={first.Seconds:F3}s unchanged={repeat.Seconds:F3}s");
    }
}
