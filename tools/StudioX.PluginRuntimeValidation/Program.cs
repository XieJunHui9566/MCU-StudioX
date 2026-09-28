using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Extensions;
using StudioX.Foundation;
using StudioX.PluginRuntimeValidation;

if (args is ["--process-plugin"] or ["--process-bad-version"])
{
    return await ProcessFixtureHost.RunAsync(args[0] == "--process-bad-version");
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var hostExecutable = Path.Combine(root, "src/StudioX.PluginHost/bin/Debug/net10.0/StudioX.PluginHost.exe");
var scratch = Path.Combine(root, "artifacts/validation/plugin-runtime-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var results = new List<string>();
var originalSecret = Environment.GetEnvironmentVariable("STUDIOX_VALIDATION_SECRET");
Environment.SetEnvironmentVariable("STUDIOX_VALIDATION_SECRET", "must-not-reach-plugin");
try
{
    foreach (var kind in new[] { "dotnet", "process" })
    {
        var pluginDirectory = Path.Combine(scratch, kind);
        Directory.CreateDirectory(pluginDirectory);
        foreach (var source in Directory.GetFiles(AppContext.BaseDirectory))
        {
            File.Copy(source, Path.Combine(pluginDirectory, Path.GetFileName(source)));
        }
        var manifest = await CreateManifestAsync(pluginDirectory, kind, kind == "process" ? ["--process-plugin"] : null);
        var manifestPath = Path.Combine(pluginDirectory, "plugin.json");
        await JsonStore.WriteAsync(manifestPath, manifest);
        var events = new ConcurrentQueue<PluginRuntimeEvent>();
        PluginRuntimeClient? client = null;
        client = await PluginRuntimeClient.StartAsync(hostExecutable, manifestPath, async (tool, input, token) =>
        {
            if (tool == "validation.reenter")
            {
                return await client!.InvokeAsync("command", "increment", input, token);
            }
            Check(tool == "validation.echo", "host tool identity");
            await Task.Delay(10, token);
            return JsonSerializer.SerializeToElement(new { echoed = input.GetProperty("value").GetInt32() });
        }, (item, _) =>
        {
            events.Enqueue(item);
            return Task.CompletedTask;
        });
        await using (client)
        {
            Check(client.Contribution.Commands.Length == 8, "contribution handshake");
            var one = await client.InvokeAsync("command", "increment", JsonSerializer.SerializeToElement(new { }));
            var two = await client.InvokeAsync("agentTool", "counter", JsonSerializer.SerializeToElement(new { }));
            Check(one.GetProperty("count").GetInt32() == 1 && two.GetProperty("count").GetInt32() == 2, "persistent state");
            var echo = await client.InvokeAsync("command", "callback", JsonSerializer.SerializeToElement(new { value = 73 }));
            Check(echo.GetProperty("echoed").GetInt32() == 73, "nested bidirectional callback");
            var environment = await client.InvokeAsync("command", "environment", JsonSerializer.SerializeToElement(new { }));
            Check(environment.GetProperty("secret").ValueKind == JsonValueKind.Null, "environment secrecy");
            await ExpectFailureAsync(() => client.InvokeAsync("command", "throw", JsonSerializer.SerializeToElement(new { })), "validation-original-error");
            await ExpectFailureAsync(() => client.InvokeAsync("command", "reenter", JsonSerializer.SerializeToElement(new { })), "PLUGIN_REENTRANT");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            try
            {
                _ = await client.InvokeAsync("command", "cancel", JsonSerializer.SerializeToElement(new { }), cancellation.Token);
                throw new InvalidOperationException("调用取消未传播。");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                results.Add(kind + ": caller cancellation propagated");
            }
            var afterCancel = await client.InvokeAsync("command", "increment", JsonSerializer.SerializeToElement(new { }));
            Check(afterCancel.GetProperty("count").GetInt32() == 3, "session survives cooperative cancellation");
            for (var iteration = 0; iteration < 200; iteration++)
            {
                _ = await client.InvokeAsync("command", "increment", JsonSerializer.SerializeToElement(new { }));
            }
            var total = await client.InvokeAsync("agentTool", "counter", JsonSerializer.SerializeToElement(new { }));
            Check(total.GetProperty("count").GetInt32() == 204, "no fixed total plugin call count limit");
            results.Add(kind + ": more than 200 sequential plugin calls remain usable without a total-call cap");
            Check(events.Any(item => item.Kind == "panel") && events.Any(item => item.Kind == "log"), "panel/log events");
            Check(client.Diagnostics.Contains("fixture Console output", StringComparison.Ordinal), "stdout/stderr separation");
            await ExpectFailureAsync(() => client.InvokeAsync("command", "crash", JsonSerializer.SerializeToElement(new { })), "PLUGIN_EXIT");
            results.Add(kind + ": persistent state, callbacks, environment, events, exception, reentry, crash passed");
        }
        if (kind == "process")
        {
            await JsonStore.WriteAsync(manifestPath, manifest with { Arguments = ["--process-bad-version"] });
            await ExpectFailureAsync(async () =>
            {
                await using var invalid = await PluginRuntimeClient.StartAsync(hostExecutable, manifestPath,
                    (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { })), (_, _) => Task.CompletedTask);
            }, "PLUGIN_PROTOCOL");
            results.Add("process: incompatible handshake rejected");
        }
    }

    results.Add(await PythonRuntimeChecks.RunAsync(root, scratch, hostExecutable));

    var legacyDirectory = Path.Combine(scratch, "legacy");
    Directory.CreateDirectory(legacyDirectory);
    var legacySource = Path.Combine(root, "examples/StudioX.SampleDecoder/bin/Debug/net10.0");
    foreach (var source in Directory.GetFiles(legacySource))
    {
        File.Copy(source, Path.Combine(legacyDirectory, Path.GetFileName(source)));
    }
    var legacyHashes = await HashDirectoryAsync(legacyDirectory);
    var legacyManifestPath = Path.Combine(legacyDirectory, "plugin.json");
    await JsonStore.WriteAsync(legacyManifestPath, new PluginManifest(1, 1, "validation.legacy", "1.0.0", "Legacy",
        "StudioX.SampleDecoder.dll", "StudioX.SampleDecoder.SensorCsvDecoder", ["decode"], legacyHashes));
    var decoded = await new PluginClient(hostExecutable).DecodeAsync(legacyManifestPath, Encoding.UTF8.GetBytes("1.2,1"));
    Check(decoded.Signals.Count > 0, "API 1 decoder preserved");
    results.Add("API 1: legacy decoder remains explicit and functional");
    await JsonStore.WriteAsync(Path.Combine(scratch, "results.json"), new { success = true, results });
    foreach (var result in results)
    {
        Console.WriteLine(result);
    }
    Console.WriteLine("Evidence: " + scratch);
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable("STUDIOX_VALIDATION_SECRET", originalSecret);
}

static void Check(bool passed, string description)
{
    if (!passed)
    {
        throw new InvalidOperationException("验证失败：" + description);
    }
}

static async Task ExpectFailureAsync(Func<Task> action, string expected)
{
    try
    {
        await action();
    }
    catch (Exception exception) when (exception.ToString().Contains(expected, StringComparison.Ordinal) ||
        exception is StudioXException studio && studio.Code == expected)
    {
        return;
    }
    throw new InvalidOperationException("未获得预期异常：" + expected);
}

static async Task<PluginManifest> CreateManifestAsync(string directory, string kind, string[]? arguments)
{
    return new PluginManifest(1, 2, "validation." + kind, "1.0.0", "Validation " + kind,
        kind == "dotnet" ? "StudioX.PluginRuntimeValidation.dll" : "", kind == "dotnet" ? typeof(ValidationPlugin).FullName! : "",
        ["commands", "panels", "agentTools"], await HashDirectoryAsync(directory), kind,
        kind == "process" ? "StudioX.PluginRuntimeValidation.exe" : null, arguments, ["validation.echo", "validation.reenter"]);
}

static async Task<Dictionary<string, string>> HashDirectoryAsync(string directory)
{
    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
    {
        await using var source = File.OpenRead(file);
        hashes[Path.GetRelativePath(directory, file).Replace('\\', '/')] = Convert.ToHexString(await SHA256.HashDataAsync(source));
    }
    return hashes;
}
