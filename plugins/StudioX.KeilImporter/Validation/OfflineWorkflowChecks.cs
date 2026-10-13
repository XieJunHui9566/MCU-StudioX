namespace StudioX.KeilImporter.Validation;

using System.Diagnostics;
using System.Text.Json;

/// <summary>使用真实子进程验证失败和取消的原始日志，无 SDK、网络或硬件依赖。</summary>
internal static class OfflineWorkflowChecks
{
    public static async Task<int> RunAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (Directory.Exists(root))
        {
            throw new InvalidOperationException("Offline evidence directory must be new.");
        }
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        var runtime = Path.Combine(root, "runtime");
        var cliDirectory = Path.Combine(runtime, "mcp-host");
        Directory.CreateDirectory(cliDirectory);
        Directory.CreateDirectory(Path.Combine(runtime, "toolsets"));
        foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
        {
            File.Copy(file, Path.Combine(cliDirectory, Path.GetFileName(file)));
        }
        var cli = Path.Combine(cliDirectory, Path.GetFileName(Environment.ProcessPath!));
        try
        {
            foreach (var mode in new[] { "failure", "cancel", "overflow" })
            {
                var project = Path.Combine(root, mode);
                Directory.CreateDirectory(Path.Combine(project, ".studiox"));
                await File.WriteAllTextAsync(Path.Combine(project, ".studiox/cli-fixture-mode"), mode);
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var clock = Stopwatch.StartNew();
                var task = PortCompilation.VerifyAsync(cli, project, cancel.Token,
                    new Dictionary<string, string> { ["src/main.c"] = new string('a', 64) });
                if (mode == "cancel")
                {
                    var ready = Path.Combine(project, ".studiox/cli-ready");
                    while (!File.Exists(ready))
                    {
                        await Task.Delay(20, cancel.Token);
                    }
                    // 进程先刷新两路原文，再写就绪标记；短等待让读通道接收这些已写出的内容。
                    await Task.Delay(200, cancel.Token);
                    cancel.Cancel();
                    try
                    {
                        await task;
                        throw new InvalidOperationException("Cancellation did not propagate.");
                    }
                    catch (OperationCanceledException) { }
                }
                else
                {
                    Check(!(await task).Verified, mode + " never reports compiled");
                }
                Check(clock.Elapsed < TimeSpan.FromSeconds(10), mode + " stops its process promptly");
                var log = await File.ReadAllTextAsync(Path.Combine(project, PortCompilation.LogPath));
                Check(log.Contains("fixture stdout", StringComparison.Ordinal) && log.Contains("fixture stderr", StringComparison.Ordinal),
                    mode + " retains both original output channels");
                using var record = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(project, ".studiox/keil-build.json")));
                Check(!record.RootElement.GetProperty("compilationVerified").GetBoolean() &&
                    record.RootElement.GetProperty("sourceHashes").GetProperty("src/main.c").GetString() == new string('a', 64),
                    mode + " saves source snapshot and failed state");
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(project, PortCompilation.LogPath))));
                Check(hash == record.RootElement.GetProperty("logSha256").GetString(), mode + " binds raw log to record");
            }
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                success = true,
                checks
            }, DeviceCatalog.Json));
            Console.WriteLine("PASS Keil offline workflow: " + checks.Count);
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "result.json"), JsonSerializer.Serialize(new
            {
                success = false,
                checks,
                error = error.ToString()
            }, DeviceCatalog.Json));
            Console.Error.WriteLine(error);
            return 1;
        }

        void Check(bool success, string name)
        {
            if (!success)
            {
                throw new InvalidOperationException(name);
            }
            checks.Add(name);
        }
    }

    public static async Task<int> CliFixtureAsync(string project, string? selectedMode = null)
    {
        var mode = selectedMode ?? await File.ReadAllTextAsync(Path.Combine(project, ".studiox/cli-fixture-mode"));
        Console.WriteLine("fixture stdout: src/main.c:2:3: error: original compiler message");
        Console.Error.WriteLine("fixture stderr: original diagnostic");
        await Console.Out.FlushAsync();
        await Console.Error.FlushAsync();
        if (mode == "cancel")
        {
            await File.WriteAllTextAsync(Path.Combine(project, ".studiox/cli-ready"), "ready");
            await Task.Delay(TimeSpan.FromMinutes(1));
        }
        if (mode == "overflow")
        {
            Console.Write(new string('x', 1024 * 1024 + 8192));
            await Console.Out.FlushAsync();
            await Task.Delay(TimeSpan.FromMinutes(1));
        }
        return 1;
    }
}
