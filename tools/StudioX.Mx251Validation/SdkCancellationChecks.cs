using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using StudioX.Engine;
using StudioX.Foundation;

internal static class SdkCancellationChecks
{
    internal static async Task RunAsync(string directory)
    {
        var root = Path.GetFullPath(directory);
        var fixture = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "fixture.json")))!;
        var builder = new BuildService(new ToolsetCatalog(fixture["tools"]!.GetValue<string>()));
        var project = Path.Combine(root, "projects/ide/calls-small");
        var cmake = Path.Combine(project, "CMakeLists.txt");
        var original = await File.ReadAllTextAsync(cmake);
        var logs = Path.Combine(root, "cancellation-checks-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(logs);
        var records = new List<object>();
        try
        {
            if (!original.Contains("set(MX251_LEAF_CALL_FEEDBACK ON)"))
            {
                await File.WriteAllTextAsync(cmake, original.Replace("include(", "set(MX251_LEAF_CALL_FEEDBACK ON)\ninclude("));
            }
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var cancellation = new CancellationTokenSource();
                var build = builder.BuildAsync(project, cancellationToken: cancellation.Token);
                var deadline = Stopwatch.StartNew();
                var observed = false;
                while (!build.IsCompleted && deadline.Elapsed < TimeSpan.FromMinutes(3))
                {
                    var latest = Path.Combine(project, ".build/mx251/latest");
                    // 老日志可能还存在：必须等本次 owner 创建后才检查第二遍日志。
                    var owner = Path.Combine(latest, "owner.json");
                    if (File.Exists(owner) && File.GetLastWriteTimeUtc(owner) > DateTime.UtcNow - deadline.Elapsed &&
                        Directory.EnumerateFiles(latest, "feedback-*.stdout.log").Any())
                    {
                        observed = true;
                        cancellation.Cancel();
                        break;
                    }
                    await Task.Delay(20);
                }
                if (!observed)
                {
                    cancellation.Cancel();
                }
                try
                {
                    await build;
                    throw new InvalidOperationException("Expected cancellation");
                }
                catch (OperationCanceledException) when (observed) { }
                foreach (var rel in new[] { ".build/firmware.hex", ".build/firmware.bin", ".build/studiox-build-receipt.json", ".build/mx251/latest/firmware.hex", ".build/mx251/latest/receipt.json" })
                {
                    if (File.Exists(Path.Combine(project, rel)))
                    {
                        throw new InvalidOperationException("Cancelled output survived: " + rel);
                    }
                }
                var recovery = await builder.BuildAsync(project);
                await File.WriteAllTextAsync(Path.Combine(logs, attempt + "-recovery.log"), recovery.Log);
                var expected = fixture["cases"]!.AsArray().Single(c => c!["id"]!.GetValue<string>() == "calls-small")!["on"]!.GetValue<string>();
                if (!recovery.Success || !Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(project, ".build/firmware.hex")))).Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Immediate recovery differs from sealed image");
                }
                records.Add(new
                {
                    attempt,
                    cancelledActualSecondPass = observed,
                    immediateRecoveryExact = true
                });
                Console.WriteLine("PASS cancelled second pass + immediate sealed recovery " + attempt);
            }
            await JsonStore.WriteAsync(Path.Combine(logs, "results.json"), new
            {
                complete = true,
                records
            });
        }
        catch (Exception error)
        {
            var processes = Process.GetProcesses().Where(p => p.ProcessName is "sdcc" or "sdas251" or "cmake" or "ninja" or "pwsh")
                .Select(p => new { p.Id, p.ProcessName }).ToArray();
            await JsonStore.WriteAsync(Path.Combine(logs, "failure.json"), new
            {
                error = error.ToString(),
                processes,
                records
            });
            throw;
        }
        finally { await File.WriteAllTextAsync(cmake, original); }
    }
}
