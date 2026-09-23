namespace StudioX.Application.CodeIntelligence;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>独立子进程上的 LSP 消息通道；UTF-8 字节分帧与 UTF-16 文本坐标分开处理。</summary>
internal sealed class LanguageServerConnection : IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reader;
    private readonly Task errors;
    private int nextId;
    private int disposed;
    public bool IsRunning => !process.HasExited && !reader.IsCompleted;

    public LanguageServerConnection(string executable, string workingDirectory, string cacheDirectory, Action<string> log)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "--background-index", "-j=2", "--clang-tidy=false", "--header-insertion=never", "--completion-style=detailed",
            "--completion-parse=always", "--use-dirty-headers", "--limit-results=100", "--log=error", "--enable-config=false", "--pch-storage=memory", "--compile-commands-dir=" + cacheDirectory }) start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new StudioXException("LANGUAGE_START", "无法启动代码提示服务。");
        reader = ReadAsync();
        errors = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line) log(line); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException ex) { log(ex.Message); }
        });
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken token = default) => SendAsync(new { jsonrpc = "2.0", method, @params = parameters }, token);
    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            if (!IsRunning) throw new StudioXException("LANGUAGE_EXITED", "代码提示服务已退出；重新打开工程可重启。");
            await SendAsync(new { jsonrpc = "2.0", id, method, @params = parameters }, token).ConfigureAwait(false);
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(12), token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                await TryCancelAsync(id).ConfigureAwait(false); throw;
            }
            catch (TimeoutException)
            {
                await TryCancelAsync(id).ConfigureAwait(false);
                throw new StudioXException("LANGUAGE_TIMEOUT", "代码提示服务响应超时，可再次按 Ctrl+Space 重试。");
            }
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task TryCancelAsync(int id)
    {
        try { await NotifyAsync("$/cancelRequest", new { id }, lifetime.Token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task SendAsync(object value, CancellationToken token)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(value);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // 一帧开始写入后只接受会话关闭取消，避免用户取消请求时留下半帧。
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.StandardInput.BaseStream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
                await process.StandardInput.BaseStream.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                // 超时后的通道可能留下半帧，必须结束这个自有子进程，不能继续拼接消息。
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                throw new StudioXException("LANGUAGE_IO_TIMEOUT", "代码提示服务通信超时；重新打开工程可重启。");
            }
        }
        finally { writer.Release(); }
    }
    private async Task ReadAsync()
    {
        Exception failure = new StudioXException("LANGUAGE_EXITED", "代码提示服务已关闭。");
        try
        {
            var stream = process.StandardOutput.BaseStream;
            var single = new byte[1];
            while (!lifetime.IsCancellationRequested)
            {
                var header = new StringBuilder();
                while (true)
                {
                    if (await stream.ReadAsync(single, lifetime.Token).ConfigureAwait(false) == 0) throw new EndOfStreamException("clangd 输出已结束。");
                    header.Append((char)single[0]);
                    if (header.Length > 8192) throw new IOException("LSP 消息头超出限制。");
                    if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
                }
                var length = header.ToString().Split("\r\n").Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Select(line => int.Parse(line[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture)).Single();
                if (length is < 0 or > 16 * 1024 * 1024) throw new IOException("LSP 消息体超出限制。");
                var body = new byte[length]; await stream.ReadExactlyAsync(body, lifetime.Token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(body); var message = json.RootElement;
                if (message.TryGetProperty("id", out var id) && !message.TryGetProperty("method", out _))
                {
                    if (id.TryGetInt32(out var number) && pending.TryRemove(number, out var completion))
                    {
                        if (message.TryGetProperty("error", out var error)) completion.TrySetException(new StudioXException("LANGUAGE_REQUEST", error.ToString()));
                        else completion.TrySetResult(message.GetProperty("result").Clone());
                    }
                }
                else if (message.TryGetProperty("id", out var requestId))
                    await SendAsync(new { jsonrpc = "2.0", id = requestId.Clone(), error = new { code = -32601, message = "Unsupported client method" } }, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { failure = ex; }
        finally { foreach (var entry in pending) if (pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(failure); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            if (IsRunning)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await RequestAsync("shutdown", null, timeout.Token).ConfigureAwait(false); await NotifyAsync("exit", null, timeout.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or StudioXException) { }
            }
            lifetime.Cancel();
            if (!process.HasExited)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().ConfigureAwait(false); }
            }
            await Task.WhenAll(reader, errors).ConfigureAwait(false);
        }
        finally { process.Dispose(); lifetime.Dispose(); writer.Dispose(); }
    }
}
