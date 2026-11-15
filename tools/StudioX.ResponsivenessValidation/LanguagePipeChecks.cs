using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StudioX.Application;
using StudioX.Foundation;

internal static class LanguagePipeChecks
{
    private static readonly Type Connection = typeof(TextSearchService).Assembly.GetType("StudioX.Application.CodeIntelligence.LanguageServerConnection")!;

    public static Task<CheckResult[]> RunAsync(string output) => Task.WhenAll(
        RunAsync(output, "write-cancel"), RunAsync(output, "queue-cancel"),
        RunAsync(output, "queue-timeout"), RunAsync(output, "response"));

    private static async Task<CheckResult> RunAsync(string output, string mode)
    {
        var watch = Stopwatch.StartNew();
        var directory = Path.Combine(output, "language-" + mode);
        Directory.CreateDirectory(directory);
        var log = new List<string>();
        var instance = Activator.CreateInstance(Connection, Environment.ProcessPath!, directory, directory,
            new Action<string>(message =>
            {
                lock (log)
                {
                    log.Add(message);
                }
            }), null)!;
        var process = (Process)Connection.GetField("process", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
        var gate = (SemaphoreSlim)Connection.GetField("writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
        var acquired = false;
        try
        {
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                await Task.Delay(10, ready.Token);
            }
            if (mode == "response")
            {
                for (var i = 0; i < 3; i++)
                {
                    var task = (Task<JsonElement>)Connection.GetMethod("RequestAsync")!.Invoke(instance, ["echo", new { text = "中文😀", i }, CancellationToken.None])!;
                    var result = await task.WaitAsync(TimeSpan.FromSeconds(3));
                    if (result.GetProperty("text").GetString() != "中文😀" || result.GetProperty("i").GetInt32() != i)
                    {
                        throw new InvalidOperationException("LSP UTF-8 framing or reply changed");
                    }
                }
            }
            else
            {
                if (mode.StartsWith("queue", StringComparison.Ordinal))
                {
                    await gate.WaitAsync();
                    acquired = true;
                }
                using var cancel = new CancellationTokenSource();
                if (mode.EndsWith("cancel", StringComparison.Ordinal))
                {
                    cancel.CancelAfter(200);
                }
                var sending = (Task)Connection.GetMethod("NotifyAsync")!.Invoke(instance, ["textDocument/didChange", new { text = new string('x', 2 * 1024 * 1024) }, cancel.Token])!;
                Exception? failure = null;
                try
                {
                    await sending.WaitAsync(TimeSpan.FromSeconds(mode == "queue-timeout" ? 7 : 2));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                var expected = mode == "queue-timeout" ? failure is StudioXException { Code: "LANGUAGE_IO_TIMEOUT" } : failure is OperationCanceledException;
                if (!expected)
                {
                    throw new InvalidOperationException("LSP send exceeded deadline or lost cancellation type", failure);
                }
                if (acquired && process.HasExited)
                {
                    throw new InvalidOperationException("queued cancellation killed another writer's process");
                }
                if (!acquired)
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            return new("language-" + mode, true, watch.ElapsedMilliseconds, mode == "response" ? "Unicode requests/replies preserved" : "blocked work bounded; cancellation and process ownership preserved");
        }
        catch (Exception ex)
        {
            return new("language-" + mode, false, watch.ElapsedMilliseconds, ex.ToString());
        }
        finally
        {
            if (acquired)
            {
                gate.Release();
            }
            if (!process.HasExited)
            {
                process.Kill(true);
            }
            await ((IAsyncDisposable)instance).DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            File.WriteAllLines(Path.Combine(directory, "channel.log"), log);
        }
    }

    public static void Child(string directory)
    {
        File.WriteAllText(Path.Combine(directory, "ready"), "ready");
        if (Path.GetFileName(directory) != "language-response")
        {
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return;
        }
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        while (true)
        {
            var header = new StringBuilder();
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                var b = input.ReadByte();
                if (b < 0)
                {
                    return;
                }
                header.Append((char)b);
            }
            var length = int.Parse(header.ToString().Split(':')[1].Trim());
            var body = new byte[length];
            input.ReadExactly(body);
            using var message = JsonDocument.Parse(body);
            if (!message.RootElement.TryGetProperty("id", out var id))
            {
                continue;
            }
            var reply = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = id.GetInt32(),
                result = message.RootElement.GetProperty("params")
            });
            output.Write(Encoding.ASCII.GetBytes($"Content-Length: {reply.Length}\r\n\r\n"));
            output.Write(reply);
            output.Flush();
        }
    }
}
