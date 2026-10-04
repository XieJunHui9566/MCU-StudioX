namespace StudioX.SecurityValidation;

using System.Text.Json;
using System.Threading.Channels;
using StudioX.Extensions;
using StudioX.Foundation;

/// <summary>对端不读取响应时，已处理请求仍占用容量，不能无限堆积响应任务。</summary>
internal static class ProtocolChecks
{
    internal static async Task RunAsync()
    {
        using var input = new QueuedReader();
        using var output = new BlockedWriter();
        using var received = new SemaphoreSlim(0);
        var count = 0;
        await using var connection = new PluginProtocolConnection(input, output, (_, _, _) =>
        {
            Interlocked.Increment(ref count);
            received.Release();
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                ok = true
            }));
        }, (_, _, _) => Task.CompletedTask);
        for (var index = 0; index < 40 && !connection.Completion.IsCompleted; index++)
        {
            input.Enqueue(JsonSerializer.Serialize(new
            {
                protocolVersion = 2,
                kind = "request",
                requestId = index.ToString(),
                method = "fixture",
                payload = new
                {
                }
            }) + "\n");
            var handled = received.WaitAsync();
            if (await Task.WhenAny(handled, connection.Completion).WaitAsync(TimeSpan.FromSeconds(3)) == connection.Completion)
            {
                break;
            }
            await handled;
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        if (!connection.Completion.IsCompleted || count > 32)
        {
            throw new InvalidOperationException("Blocked output bypassed inbound capacity: " + count);
        }
        try
        {
            await connection.Completion;
            throw new InvalidOperationException("Expected bounded protocol failure.");
        }
        catch (StudioXException error) when (error.Code == "PLUGIN_BUSY")
        {
            Console.WriteLine("PASS blocked response rejects request flood at " + count + " in-flight requests");
        }
    }

    private sealed class QueuedReader : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        private string remaining = "";
        internal void Enqueue(string text) => lines.Writer.TryWrite(text);
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default)
        {
            if (remaining.Length == 0)
            {
                remaining = await lines.Reader.ReadAsync(token);
            }
            var count = Math.Min(buffer.Length, remaining.Length);
            remaining.AsMemory(0, count).CopyTo(buffer);
            remaining = remaining[count..];
            return count;
        }
    }

    private sealed class BlockedWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
    }
}
