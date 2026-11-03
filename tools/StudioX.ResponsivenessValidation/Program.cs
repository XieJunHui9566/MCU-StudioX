using System.Diagnostics;
using System.Text.Json;

/// <summary>只使用本工具启动的假调试器和隔离日志；不取得探针租约，也不连接硬件。</summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--child", var mode])
        {
            RunChild(mode);
            return 0;
        }
        var output = Path.GetFullPath(args.FirstOrDefault() ?? ".artifacts/responsiveness-validation");
        Directory.CreateDirectory(output);
        var results = new List<CheckResult> { OutputChecks.Run() };
        SynchronizationContext.SetSynchronizationContext(null);
        results.AddRange(DebuggerPipeChecks.RunAsync(output).GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
        {
            hardware = false,
            passed = results.All(result => result.Passed),
            results
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var result in results)
        {
            Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name}: {result.Detail} ({result.ElapsedMs} ms)");
        }
        return results.All(result => result.Passed) ? 0 : 1;
    }

    private static void RunChild(string mode)
    {
        Console.WriteLine("ready");
        if (mode == "idle")
        {
            Thread.Sleep(TimeSpan.FromSeconds(90));
            return;
        }
        while (Console.ReadLine() is { } line)
        {
            if (line == "quit") { return; }
            if (mode == "mdb" && line.StartsWith("echo ", StringComparison.Ordinal))
            {
                Console.WriteLine("/*" + line[5..] + "*/");
            }
            if (mode == "gdb" && line.IndexOf('-') is var separator && separator > 0)
            {
                Console.WriteLine(line[..separator] + "^done");
            }
        }
    }

    internal static ProcessStartInfo ChildStart(string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--child");
        start.ArgumentList.Add(mode);
        return start;
    }
}

internal sealed record CheckResult(string Name, bool Passed, long ElapsedMs, string Detail);
