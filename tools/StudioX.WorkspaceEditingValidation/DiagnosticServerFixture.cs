using System.Text;
using System.Text.Json;
using StudioX.Application.CodeIntelligence;

/// <summary>仅用于协议故障验收的子进程，不声称模拟 C/C++ 语义。</summary>
internal static class DiagnosticServerFixture
{
    public static async Task RunAsync()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var documents = new Dictionary<string, (int Version, string Text)>();
        await File.WriteAllTextAsync(".fixture-pid", Environment.ProcessId.ToString());
        async Task Send(object message)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            await output.WriteAsync(body);
            await output.FlushAsync();
        }
        async Task Publish(string uri)
        {
            var (version, text) = documents[uri];
            if (text.Contains("FAKE_INCLUDE_LOG", StringComparison.Ordinal))
            {
                await Console.Error.WriteLineAsync("clangd: IncludeCleaner: Failed to get an entry for resolved path L: no such file or directory");
                await Console.Error.WriteLineAsync("clangd: IncludeCleaner: Failed to get an entry for resolved path LED: no such file or directory");
            }
            var offset = text.IndexOf("FAKE_REAL_ERROR", StringComparison.Ordinal);
            object Error(string message, object? range = null) => new
            {
                range = range ?? new
                {
                    start = CodePositions.FromOffset(text, Math.Max(0, offset)),
                    end = CodePositions.FromOffset(text, Math.Max(0, offset) + (offset < 0 ? 0 : 15))
                },
                severity = 1,
                source = "fixture",
                message
            };
            async Task Batch(int publishedVersion, object[] items) => await Send(new
            {
                jsonrpc = "2.0",
                method = "textDocument/publishDiagnostics",
                @params = new
                {
                    uri,
                    version = publishedVersion,
                    diagnostics = items
                }
            });
            if (text.Contains("FAKE_OLD_VERSION", StringComparison.Ordinal))
            {
                await Batch(version - 1, [Error("STALE_VERSION")]);
            }
            if (text.Contains("FAKE_FUTURE_VERSION", StringComparison.Ordinal))
            {
                await Batch(version + 1, [Error("FUTURE_VERSION")]);
            }
            if (text.Contains("FAKE_NO_VERSION", StringComparison.Ordinal))
            {
                await Send(new
                {
                    jsonrpc = "2.0",
                    method = "textDocument/publishDiagnostics",
                    @params = new
                    {
                        uri,
                        diagnostics = new[] { Error("UNVERSIONED") }
                    }
                });
            }
            var items = new List<object>();
            if (text.Contains("FAKE_BAD_RANGE", StringComparison.Ordinal))
            {
                items.Add(Error("OUT_OF_RANGE", new
                {
                    start = new
                    {
                        line = 999,
                        character = 0
                    },
                    end = new
                    {
                        line = 999,
                        character = 1
                    }
                }));
                items.Add(Error("NULL_RANGE", new
                {
                    start = (object?)null,
                    end = (object?)null
                }));
            }
            if (offset >= 0)
            {
                items.Add(Error("FAKE_REAL_ERROR"));
            }
            await Batch(version, items.ToArray());
        }
        while (true)
        {
            var header = new StringBuilder();
            var single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await input.ReadAsync(single) == 0)
                {
                    return;
                }
                header.Append((char)single[0]);
                if (header.Length > 8192)
                {
                    throw new IOException("Fixture header overflow.");
                }
            }
            var length = int.Parse(header.ToString().Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..]);
            var body = new byte[length];
            await input.ReadExactlyAsync(body);
            using var json = JsonDocument.Parse(body);
            var message = json.RootElement;
            var method = message.GetProperty("method").GetString();
            var parameters = message.GetProperty("params");
            if (method == "exit")
            {
                return;
            }
            if (method == "initialize" && File.Exists(".fixture-init-delay"))
            {
                await File.WriteAllTextAsync(".fixture-initializing", "ready");
                await Task.Delay(500);
            }
            if (method == "textDocument/didOpen" || method == "textDocument/didChange")
            {
                var document = parameters.GetProperty("textDocument");
                var uri = document.GetProperty("uri").GetString()!;
                var text = method == "textDocument/didOpen" ? document.GetProperty("text").GetString()! : parameters.GetProperty("contentChanges")[0].GetProperty("text").GetString()!;
                documents[uri] = (document.GetProperty("version").GetInt32(), text);
                await Publish(uri);
            }
            if (!message.TryGetProperty("id", out var id))
            {
                continue;
            }
            if (method == "textDocument/documentSymbol")
            {
                var uri = parameters.GetProperty("textDocument").GetProperty("uri").GetString()!;
                if (documents[uri].Text.Contains("FAKE_DELAY", StringComparison.Ordinal))
                {
                    await File.WriteAllTextAsync(".fixture-waiting", "ready");
                    await Task.Delay(500);
                    await Publish(uri);
                }
                if (documents[uri].Text.Contains("FAKE_BAD_PROTOCOL", StringComparison.Ordinal))
                {
                    await output.WriteAsync(Encoding.ASCII.GetBytes("Content-Length: 1\r\n\r\n{"));
                    await output.FlushAsync();
                    continue;
                }
            }
            await Send(new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                result = method == "initialize" ? (object)new
                {
                    capabilities = new
                    {
                        positionEncoding = "utf-16"
                    }
                } : method == "shutdown" ? null : (object)Array.Empty<object>()
            });
        }
    }
}
