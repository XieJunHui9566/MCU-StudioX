namespace StudioX.Extensions;

using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>JSON 行协议的双向关联与取消；读取循环独立于工具处理，避免嵌套回调死锁。</summary>
public sealed class PluginProtocolConnection : IAsyncDisposable
{
    private const int MaximumLineLength = 1024 * 1024;
    private readonly TextReader input;
    private readonly TextWriter output;
    private readonly Func<string, JsonElement, CancellationToken, Task<JsonElement>> onRequest;
    private readonly Func<string, JsonElement, CancellationToken, Task> onEvent;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writerGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> incoming = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> handlers = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
    private readonly Task reader;
    private Exception? terminationError;
    private int disposed;

    /// <summary>构建后立即读取；所有输出由同一信号量串行写入。</summary>
    public PluginProtocolConnection(
        TextReader input,
        TextWriter output,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> onRequest,
        Func<string, JsonElement, CancellationToken, Task> onEvent)
    {
        this.input = input;
        this.output = output;
        this.onRequest = onRequest;
        this.onEvent = onEvent;
        reader = Task.Run(ReadAsync);
    }

    /// <summary>协议关闭时完成；异常保留进程或协议的原始诊断。</summary>
    public Task Completion => reader;

    /// <summary>单次调用拥有独立超时；取消通知对端，并移除响应等待者。</summary>
    public async Task<JsonElement> RequestAsync(string method, JsonElement payload, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (pending.Count >= 32)
        {
            throw new StudioXException("PLUGIN_BUSY", "插件在途请求超过限制。");
        }
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("重复请求 ID。");
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(timeout);
        try
        {
            await SendAsync(new PluginProtocolMessage(2, "request", id, method, payload), deadline.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested)
            {
                try
                {
                    using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await SendAsync(new PluginProtocolMessage(2, "cancel", id), cancelDeadline.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // 对端可能已经退出；调用者的取消语义优先，关闭诊断由 Completion 保留。
                }
            }
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            if (lifetime.IsCancellationRequested)
            {
                if (terminationError is { } protocolError)
                {
                    ExceptionDispatchInfo.Capture(protocolError).Throw();
                }
                throw new StudioXException("PLUGIN_EXIT", "插件协议连接已结束。");
            }
            throw new StudioXException("PLUGIN_TIMEOUT", $"插件调用超时：{method}");
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    /// <summary>发布 panel 或 log 等无需响应的数据事件。</summary>
    public Task PublishAsync(string kind, JsonElement payload, CancellationToken cancellationToken)
    {
        return SendAsync(new PluginProtocolMessage(2, "event", Method: kind, Payload: payload), cancellationToken);
    }

    /// <summary>等待已读取的回调快照，保证激活响应之前的面板事件已经交付应用。</summary>
    public Task DrainHandlersAsync(CancellationToken cancellationToken)
    {
        return Task.WhenAll(handlers.Values.ToArray()).WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await reader.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or JsonException or StudioXException or OperationCanceledException or ObjectDisposedException)
        {
            // 调用等待者已经取得关闭异常；清理不重复抛出同一个协议错误。
        }
        var running = handlers.Values.ToArray();
        try
        {
            await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 用户插件可能忽略取消；进程所有者在 Dispose 后终止整个进程树。
        }
        // 回调可能忽略取消，信号量与 lifetime 由仍在清理的异步操作使用，不提前释放。
    }

    private async Task SendAsync(PluginProtocolMessage message, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message, options);
        if (line.Length > MaximumLineLength)
        {
            throw new StudioXException("PLUGIN_PROTOCOL_LIMIT", "插件协议消息超过 1 MiB 字符限制。");
        }
        await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writerGate.Release();
        }
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            var buffer = new char[4096];
            var line = new StringBuilder();
            while (!lifetime.IsCancellationRequested)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new StudioXException("PLUGIN_EXIT", "插件标准输出已关闭。");
                }
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        Dispatch(line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    else
                    {
                        line.Append(buffer[index]);
                        if (line.Length > MaximumLineLength)
                        {
                            throw new StudioXException("PLUGIN_PROTOCOL_LIMIT", "插件协议行超过限制。");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // 生命周期关闭属于正常清理，仍需取消全部在途双向调用。
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            var error = failure ?? new StudioXException("PLUGIN_EXIT", "插件协议连接已结束。");
            terminationError ??= error;
            foreach (var completion in pending.Values)
            {
                completion.TrySetException(error);
            }
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private void Dispatch(string line)
    {
        var message = JsonSerializer.Deserialize<PluginProtocolMessage>(line, options);
        if (message is null || message.ProtocolVersion != 2 || string.IsNullOrWhiteSpace(message.Kind))
        {
            throw new StudioXException("PLUGIN_PROTOCOL", "插件协议版本或消息无效。");
        }
        if (message.Kind == "response" && !string.IsNullOrWhiteSpace(message.RequestId))
        {
            if (pending.TryGetValue(message.RequestId, out var completion))
            {
                if (message.ErrorCode is not null)
                {
                    completion.TrySetException(new StudioXException(message.ErrorCode, message.Error ?? "插件调用失败。"));
                }
                else if (message.Payload is { } payload)
                {
                    completion.TrySetResult(payload);
                }
                else
                {
                    completion.TrySetException(new StudioXException("PLUGIN_PROTOCOL", "插件响应缺少结果。"));
                }
            }
            // 对端在取消通知前完成的迟到响应不再交付给其他请求。
            return;
        }
        if (message.Kind == "cancel" && !string.IsNullOrWhiteSpace(message.RequestId))
        {
            if (incoming.TryGetValue(message.RequestId, out var cancellation))
            {
                cancellation.Cancel();
            }
            return;
        }
        if (message.Kind == "request" && !string.IsNullOrWhiteSpace(message.RequestId) && !string.IsNullOrWhiteSpace(message.Method))
        {
            if (incoming.Count >= 32)
            {
                throw new StudioXException("PLUGIN_BUSY", "插件入站请求超过限制。");
            }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            cancellation.CancelAfter(TimeSpan.FromMinutes(15));
            if (!incoming.TryAdd(message.RequestId, cancellation))
            {
                cancellation.Dispose();
                throw new StudioXException("PLUGIN_PROTOCOL", "插件复用了在途请求 ID。");
            }
            // 回调生命周期不使用对端提供的 ID，避免响应完成前复用 ID 覆盖旧任务。
            Track(Guid.NewGuid().ToString("N"), () => HandleRequestAsync(message, cancellation));
            return;
        }
        if (message.Kind == "event" && !string.IsNullOrWhiteSpace(message.Method) && message.Payload is { } eventPayload)
        {
            if (handlers.Count >= 64)
            {
                throw new StudioXException("PLUGIN_BUSY", "插件事件队列超过限制。");
            }
            Track(Guid.NewGuid().ToString("N"), () => onEvent(message.Method, eventPayload, lifetime.Token));
            return;
        }
        throw new StudioXException("PLUGIN_PROTOCOL", "插件消息种类或必填字段无效。");
    }

    private void Track(string id, Func<Task> action)
    {
        var task = Task.Run(action);
        handlers[id] = task;
        _ = task.ContinueWith(completed =>
        {
            handlers.TryRemove(id, out _);
            if (completed.IsFaulted)
            {
                terminationError = completed.Exception!.GetBaseException();
                foreach (var completion in pending.Values)
                {
                    completion.TrySetException(completed.Exception!.GetBaseException());
                }
                lifetime.Cancel();
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task HandleRequestAsync(PluginProtocolMessage message, CancellationTokenSource cancellation)
    {
        try
        {
            PluginProtocolMessage response;
            try
            {
                var result = await onRequest(message.Method!, message.Payload ?? JsonSerializer.SerializeToElement(new
                {
                }), cancellation.Token)
                    .ConfigureAwait(false);
                response = new PluginProtocolMessage(2, "response", message.RequestId, Payload: result);
            }
            catch (Exception exception)
            {
                var code = exception is StudioXException studio ? studio.Code : exception is OperationCanceledException ? "PLUGIN_CANCELLED" : "PLUGIN_EXCEPTION";
                response = new PluginProtocolMessage(2, "response", message.RequestId, ErrorCode: code, Error: exception.ToString());
            }
            // 对端不读取响应时仍占用入站额度，防止已处理请求无限堆积在写入信号量后面。
            await SendAsync(response, lifetime.Token).ConfigureAwait(false);
        }
        finally
        {
            incoming.TryRemove(message.RequestId!, out _);
            cancellation.Dispose();
        }
    }
}
