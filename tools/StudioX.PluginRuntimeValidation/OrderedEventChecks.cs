namespace StudioX.PluginRuntimeValidation;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;
using StudioX.Foundation;

/// <summary>用可控协议管道验证事件顺序、双向回调和退出边界，不启动设备或外部服务。</summary>
internal static class OrderedEventChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    public static async Task<string> RunAsync(string output, Action<bool, string> check)
    {
        Directory.CreateDirectory(output);
        await CheckOrderingAndDrainAsync(check);
        await CheckBidirectionalEventAsync(check);
        await CheckFaultAsync(check);
        await CheckBufferedCancellationFaultAsync(check);
        await CheckUnexpectedEventCancellationAsync(check);
        await CheckShutdownAsync(check);
        await CheckOverflowAsync(check);
        const string summary = "Ordered events: wire order, drain snapshot/cancellation, bidirectional callback, original fault including buffered read cancellation race, unexpected callback cancellation, cooperative shutdown and 64-event overflow passed";
        await JsonStore.WriteAsync(Path.Combine(output, "ordered-events.json"), new
        {
            success = true,
            summary
        });
        return summary;
    }

    private static async Task CheckOrderingAndDrainAsync(Action<bool, string> check)
    {
        var firstEntered = Signal();
        var releaseFirst = Signal();
        var secondEntered = Signal();
        var thirdEntered = Signal();
        var releaseThird = Signal();
        var barrier = Signal();
        var finished = new ConcurrentQueue<int>();
        await using var peer = new Peer((_, _, _) =>
        {
            barrier.TrySetResult(true);
            return Task.FromResult(Empty);
        }, async (_, payload, token) =>
        {
            var value = payload.GetProperty("value").GetInt32();
            if (value == 1)
            {
                firstEntered.TrySetResult(true);
                await releaseFirst.Task.WaitAsync(token);
            }
            else if (value == 2)
            {
                secondEntered.TrySetResult(true);
            }
            else
            {
                thirdEntered.TrySetResult(true);
                await releaseThird.Task.WaitAsync(token);
            }
            finished.Enqueue(value);
        });
        peer.Event(1);
        await firstEntered.Task.WaitAsync(Deadline);
        peer.Event(2);
        peer.Request("barrier");
        await barrier.Task.WaitAsync(Deadline);
        // 首个回调仍被门闩挡住；旧版会在此期间完成第二个回调，得到 2、1。
        var raced = await Task.WhenAny(secondEntered.Task, Task.Delay(150));
        check(raced != secondEntered.Task, "event 2 cannot overtake blocked event 1");
        var snapshot = peer.Connection.DrainHandlersAsync(CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        var cancelledDrain = peer.Connection.DrainHandlersAsync(cancelled.Token);
        cancelled.Cancel();
        await ExpectCancellationAsync(cancelledDrain);
        check(!peer.Connection.Completion.IsCompleted, "cancelled drain does not close the connection");
        peer.Event(3);
        releaseFirst.TrySetResult(true);
        await thirdEntered.Task.WaitAsync(Deadline);
        await snapshot.WaitAsync(Deadline);
        check(finished.SequenceEqual(new[] { 1, 2 }), "drain captures only previously read events in wire order");
        releaseThird.TrySetResult(true);
        await peer.Connection.DrainHandlersAsync(CancellationToken.None).WaitAsync(Deadline);
        check(finished.SequenceEqual(new[] { 1, 2, 3 }), "all panel states delivered without dropping intermediate updates");
    }

    private static async Task CheckBidirectionalEventAsync(Action<bool, string> check)
    {
        var completed = Signal();
        Peer? peer = null;
        peer = new Peer((_, _, _) => Task.FromResult(Empty), async (_, _, token) =>
        {
            var result = await peer!.Connection.RequestAsync("hostCall", Empty, Deadline, token);
            check(result.GetProperty("value").GetInt32() == 73, "event callback receives bidirectional response");
            completed.TrySetResult(true);
        });
        await using (peer)
        {
            peer.Event(1);
            var request = await peer.OutputAsync();
            check(request.Kind == "request" && request.Method == "hostCall", "event callback can issue hostCall");
            peer.Send(new PluginProtocolMessage(2, "response", request.RequestId,
                Payload: JsonSerializer.SerializeToElement(new
                {
                    value = 73
                })));
            await completed.Task.WaitAsync(Deadline);
            await peer.Connection.DrainHandlersAsync(CancellationToken.None).WaitAsync(Deadline);
            check(!peer.Connection.Completion.IsCompleted, "event hostCall does not block the protocol reader");
        }
    }

    private static async Task CheckFaultAsync(Action<bool, string> check)
    {
        const string marker = "ordered-event-original-failure";
        var laterInvoked = false;
        await using var peer = new Peer((_, _, _) => Task.FromResult(Empty), (_, payload, _) =>
        {
            if (payload.GetProperty("value").GetInt32() == 1)
            {
                throw new InvalidOperationException(marker);
            }
            laterInvoked = true;
            return Task.CompletedTask;
        });
        var pending = peer.Connection.RequestAsync("pending", Empty, Deadline, CancellationToken.None);
        _ = await peer.OutputAsync();
        peer.Event(1);
        peer.Event(2);
        await ExpectFailureAsync(pending, marker);
        await ExpectFailureAsync(peer.Connection.Completion, marker);
        await ExpectFailureAsync(peer.Connection.DrainHandlersAsync(CancellationToken.None), marker);
        check(!laterInvoked, "event failure terminates the queue without applying later stale states");
    }

    private static async Task CheckBufferedCancellationFaultAsync(Action<bool, string> check)
    {
        const string marker = "buffer-returned-after-callback-fault";
        var input = new CancellationRaceInput();
        var output = new Output();
        await using var connection = new PluginProtocolConnection(input, output,
            (_, _, _) => Task.FromResult(Empty), async (_, _, _) =>
            {
                await input.SecondReadEntered.Task;
                throw new InvalidOperationException(marker);
            });
        await ExpectFailureAsync(connection.Completion, marker);
        await ExpectFailureAsync(connection.DrainHandlersAsync(CancellationToken.None), marker);
        check(input.ReturnedAfterCancellation, "buffer can be returned after callback cancellation instead of throwing OCE");
        check(connection.Completion.IsFaulted, "normal while exit preserves original callback failure in Completion");
    }

    private static async Task CheckUnexpectedEventCancellationAsync(Action<bool, string> check)
    {
        const string marker = "callback-cancelled-without-lifetime-cancellation";
        var laterInvoked = false;
        await using var peer = new Peer((_, _, _) => Task.FromResult(Empty), (_, payload, _) =>
        {
            if (payload.GetProperty("value").GetInt32() == 1)
            {
                throw new OperationCanceledException(marker, new CancellationToken(true));
            }
            laterInvoked = true;
            return Task.CompletedTask;
        });
        var pending = peer.Connection.RequestAsync("pending", Empty, Deadline, CancellationToken.None);
        _ = await peer.OutputAsync();
        peer.Event(1);
        peer.Event(2);
        await ExpectFailureAsync(pending, "PLUGIN_EVENT_CANCELLED");
        try
        {
            await peer.Connection.Completion.WaitAsync(Deadline);
            throw new InvalidOperationException("Unexpected event cancellation left the connection active.");
        }
        catch (StudioXException exception) when (exception.Code == "PLUGIN_EVENT_CANCELLED")
        {
            check(exception.InnerException is OperationCanceledException original && original.Message == marker,
                "unexpected callback cancellation exposes stable code and original InnerException");
        }
        await ExpectFailureAsync(peer.Connection.DrainHandlersAsync(CancellationToken.None), "PLUGIN_EVENT_CANCELLED");
        check(!laterInvoked && peer.Connection.Completion.IsFaulted,
            "unexpected callback cancellation terminates the session rather than silently poisoning later events");
    }

    private static async Task CheckShutdownAsync(Action<bool, string> check)
    {
        var entered = Signal();
        var observedCancellation = Signal();
        var barrier = Signal();
        var invoked = 0;
        await using var peer = new Peer((_, _, _) =>
        {
            barrier.TrySetResult(true);
            return Task.FromResult(Empty);
        }, async (_, _, token) =>
        {
            Interlocked.Increment(ref invoked);
            entered.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                observedCancellation.TrySetResult(true);
                throw;
            }
        });
        peer.Event(1);
        await entered.Task.WaitAsync(Deadline);
        peer.Event(2);
        peer.Request("barrier");
        await barrier.Task.WaitAsync(Deadline);
        var drain = peer.Connection.DrainHandlersAsync(CancellationToken.None);
        await peer.Connection.DisposeAsync().AsTask().WaitAsync(Deadline);
        await observedCancellation.Task.WaitAsync(Deadline);
        await ExpectCancellationAsync(drain);
        check(invoked == 1, "shutdown cancels the active callback and never invokes queued callbacks");
    }

    private static async Task CheckOverflowAsync(Action<bool, string> check)
    {
        var entered = Signal();
        var barrier = Signal();
        var invoked = 0;
        await using var peer = new Peer((_, _, _) =>
        {
            barrier.TrySetResult(true);
            return Task.FromResult(Empty);
        }, async (_, _, token) =>
        {
            Interlocked.Increment(ref invoked);
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        peer.Event(1);
        await entered.Task.WaitAsync(Deadline);
        for (var value = 2; value <= 64; value++)
        {
            peer.Event(value);
        }
        peer.Request("barrier");
        await barrier.Task.WaitAsync(Deadline);
        check(!peer.Connection.Completion.IsCompleted, "exactly 64 active/queued events are accepted");
        peer.Event(65);
        await ExpectFailureAsync(peer.Connection.Completion, "PLUGIN_BUSY");
        check(invoked == 1, "event overflow is explicit and does not run the queued states concurrently");
    }

    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ExpectFailureAsync(Task task, string diagnostic)
    {
        try
        {
            await task.WaitAsync(Deadline);
        }
        catch (Exception exception) when (exception is not TimeoutException &&
            (exception.ToString().Contains(diagnostic, StringComparison.Ordinal) || exception is StudioXException studio && studio.Code == diagnostic))
        {
            return;
        }
        throw new InvalidOperationException("Expected protocol failure: " + diagnostic);
    }

    private static async Task ExpectCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Deadline);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Expected cooperative cancellation.");
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly Input input = new();
        private readonly Output output = new();
        private readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        public PluginProtocolConnection Connection
        {
            get;
        }

        public Peer(Func<string, JsonElement, CancellationToken, Task<JsonElement>> request,
            Func<string, JsonElement, CancellationToken, Task> callback)
        {
            Connection = new PluginProtocolConnection(input, output, request, callback);
        }

        public void Event(int value) => Send(new PluginProtocolMessage(2, "event", Method: "panel",
            Payload: JsonSerializer.SerializeToElement(new
            {
                value
            })));

        public void Request(string method) => Send(new PluginProtocolMessage(2, "request", Guid.NewGuid().ToString("N"), method, Empty));

        public void Send(PluginProtocolMessage message) => input.Write(JsonSerializer.Serialize(message, options) + "\n");

        public async Task<PluginProtocolMessage> OutputAsync()
        {
            using var timeout = new CancellationTokenSource(Deadline);
            return JsonSerializer.Deserialize<PluginProtocolMessage>(await output.Lines.Reader.ReadAsync(timeout.Token), options)
                ?? throw new InvalidOperationException("Empty output message.");
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private sealed class Input : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        private string? current;
        private int offset;

        public void Write(string text)
        {
            if (!lines.Writer.TryWrite(text))
            {
                throw new InvalidOperationException("Protocol fixture input is closed.");
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            current ??= await lines.Reader.ReadAsync(cancellationToken);
            var length = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, length).CopyTo(buffer);
            offset += length;
            if (offset == current.Length)
            {
                current = null;
                offset = 0;
            }
            return length;
        }
    }

    private sealed class CancellationRaceInput : TextReader
    {
        private int reads;
        public TaskCompletionSource<bool> SecondReadEntered { get; } = Signal();
        public bool ReturnedAfterCancellation
        {
            get; private set;
        }

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                const string line = "{\"protocolVersion\":2,\"kind\":\"event\",\"method\":\"panel\",\"payload\":{}}\n";
                line.AsMemory().CopyTo(buffer);
                return line.Length;
            }
            var cancelled = Signal();
            using var registration = cancellationToken.Register(() => cancelled.TrySetResult(true));
            SecondReadEntered.TrySetResult(true);
            await cancelled.Task;
            // 合法的读取竞态：已有数据先于取消交付，下一轮 while 才观察取消。
            buffer.Span[0] = ' ';
            ReturnedAfterCancellation = cancellationToken.IsCancellationRequested;
            return 1;
        }
    }

    private sealed class Output : TextWriter
    {
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Lines.Writer.TryWrite(buffer.ToString()))
            {
                throw new IOException("Protocol fixture output is closed.");
            }
            return Task.CompletedTask;
        }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

}
