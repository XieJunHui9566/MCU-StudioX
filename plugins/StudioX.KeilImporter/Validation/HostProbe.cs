namespace StudioX.KeilImporter.Validation;

using System.Diagnostics;
using System.Text.Json;

/// <summary>实际安装版独立插件宿主的协议探针，不替换 IDE 契约。</summary>
internal sealed class HostProbe : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> stderr;
    private int requestNumber;
    public List<JsonElement> Panels { get; } = [];

    public HostProbe(string executable, string manifest)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--extension");
        start.ArgumentList.Add(manifest);
        process = Process.Start(start) ?? throw new InvalidOperationException("Host did not start.");
        stderr = process.StandardError.ReadToEndAsync();
    }

    public async Task<JsonElement> RequestAsync(string method, object payload)
    {
        var id = "keil-validation-" + ++requestNumber;
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { protocolVersion = 2, kind = "request", requestId = id, method, payload }));
        await process.StandardInput.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException("Host exited: " + await stderr);
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.GetProperty("kind").GetString() == "event")
            {
                if (message.TryGetProperty("method", out var eventName) && eventName.GetString() == "panel")
                {
                    Panels.Add(message.GetProperty("payload").Clone());
                }
                continue;
            }
            if (message.GetProperty("kind").GetString() == "response" && message.GetProperty("requestId").GetString() == id)
            {
                if (message.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    throw new InvalidOperationException(code.GetString() + ": " + message.GetProperty("error").GetString());
                }
                return message.GetProperty("payload").Clone();
            }
            throw new InvalidOperationException("Unexpected protocol message: " + line);
        }
    }

    public Task<JsonElement> InvokeAsync(string id, Dictionary<string, object?> fields) =>
        RequestAsync("invoke", new { kind = "command", id, arguments = new { widgetId = "form", values = fields } });

    public async ValueTask DisposeAsync()
    {
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        process.Dispose();
    }
}
