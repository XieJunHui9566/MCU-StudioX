namespace StudioX.Extensions;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StudioX.Extensions.Abstractions;

/// <summary>一个解析会话一个持久宿主；调用由应用层单线程排队，绝不占用桌面线程。</summary>
public sealed class SerialScriptClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly StringBuilder errors = new();
    private readonly Task stderr, monitor;
    private long id;
    private string? termination;
    private SerialScriptClient(string executable)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("未找到随 IDE 安装的脚本宿主。", executable);
        process = new Process { StartInfo = new(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        } };
        process.StartInfo.ArgumentList.Add("--serial-script"); process.Start();
        stderr = DrainErrorsAsync(); monitor = MonitorAsync();
    }
    public static async Task<SerialScriptClient> StartAsync(string executable, string source, CancellationToken token = default)
    {
        var client = new SerialScriptClient(executable);
        try { await client.ExchangeAsync(new(1, 0, "init", Source: source), token); return client; }
        catch { await client.DisposeAsync(); throw; }
    }
    public Task ResetAsync(CancellationToken token) => ExchangeAsync(new(1, 0, "reset"), token);
    public async Task<SerialScriptResult> DecodeAsync(byte[] bytes, string direction, DateTimeOffset time, bool flush, CancellationToken token = default)
    {
        if (bytes.Length > SerialScriptContract.MaxInput) throw new ArgumentException("脚本缓冲超过 8192 B。");
        var result = (await ExchangeAsync(new(1, 0, "decode", Bytes: bytes.Select(b => (int)b).ToArray(), Direction: direction, Timestamp: time.ToString("O"), Flush: flush), token)).Result;
        SerialScriptContract.Validate(result, bytes.Length); return result!;
    }
    private async Task<SerialScriptResponse> ExchangeAsync(SerialScriptRequest request, CancellationToken token)
    {
        request = request with { Id = ++id };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token, lifetime.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, SerialScriptContract.Json).AsMemory(), linked.Token);
            await process.StandardInput.FlushAsync(linked.Token);
            var line = await SerialScriptContract.ReadLineAsync(process.StandardOutput, linked.Token) ?? throw new EndOfStreamException("脚本宿主已退出。");
            var response = JsonSerializer.Deserialize<SerialScriptResponse>(line, SerialScriptContract.Json);
            if (response is null || response.ApiVersion != 1 || response.Id != request.Id) throw new InvalidDataException("脚本宿主响应版本或序号无效。");
            if (response.Error is not null) throw new InvalidDataException(response.Error);
            return response;
        }
        catch (Exception ex)
        {
            Kill();
            if (token.IsCancellationRequested) throw new OperationCanceledException(token);
            string diagnostic; lock (errors) diagnostic = errors.ToString();
            throw new IOException((termination ?? (timeout.IsCancellationRequested ? "脚本执行超时，宿主已终止。" : "脚本解析已停止：" + ex.Message)) + "\n" + diagnostic, ex);
        }
    }
    private async Task DrainErrorsAsync()
    {
        var buffer = new char[1024];
        try { int n; while ((n = await process.StandardError.ReadAsync(buffer.AsMemory(), lifetime.Token)) > 0)
            lock (errors) { errors.Append(buffer, 0, n); if (errors.Length > 8192) errors.Remove(0, errors.Length - 8192); } }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (IOException ex) { lock (errors) errors.Append(ex.Message); }
    }
    private async Task MonitorAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(100, lifetime.Token); if (process.HasExited) return;
                process.Refresh(); if (process.WorkingSet64 > 256L * 1024 * 1024)
                { termination = "脚本宿主超过 256 MiB 内存限制，已终止。"; Kill(); return; }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (InvalidOperationException) { /* 宿主恰好退出。 */ }
    }
    private void Kill() { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync(); Kill(); await Task.WhenAll(stderr, monitor);
        await process.WaitForExitAsync(); process.Dispose(); lifetime.Dispose();
    }
}
