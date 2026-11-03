using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using StudioX.Application.Serial;
using StudioX.Devices;
using StudioX.Foundation;

internal static class LifecycleChecks
{
    public static async Task<string> SerialAsync(string output)
    {
        await using var hub = new DeviceHub();
        var fake = new FakeTransport();
        await using (var service = new SerialTerminalService(hub, output, _ => fake))
        {
            await service.ConnectAsync(new("COM99")).WaitAsync(TimeSpan.FromSeconds(2));
            var bytes = Encoding.UTF8.GetBytes("\x1b[32mdiagnostic\x1b[0m\r\n");
            for (var i = 0; i < 1000; i++)
            {
                fake.Packets.Writer.TryWrite(bytes);
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (service.Status.Received != bytes.Length * 1000)
            {
                await Task.Delay(5, deadline.Token);
            }
            _ = service.ReadDisplay(-1, false);
            await service.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(2));
            if (service.Status.Connected || !fake.Closed)
            {
                throw new InvalidOperationException("模拟串口没有释放。");
            }
        }
        var blocked = new FakeTransport { BlockSend = true };
        var session = await hub.OpenAsync(blocked);
        var sending = session.SendAsync(new byte[] { 1 }).AsTask();
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await sending;
            throw new InvalidOperationException("已关闭会话仍完成了阻塞发送。");
        }
        catch (OperationCanceledException) { }
        if (!blocked.Closed)
        {
            throw new InvalidOperationException("发送取消后没有释放模拟传输。");
        }
        return "1000 ANSI frames, disconnect, blocked TX cancellation, ownership released";
    }

    public static async Task<string> TerminalAsync()
    {
        await using var terminal = new PseudoConsoleProcess();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Output += text => { if (text.Contains("ready", StringComparison.Ordinal)) { ready.TrySetResult(); } };
        await Task.Run(() => terminal.Start(Environment.ProcessPath!, "--child idle", Environment.CurrentDirectory,
            new Dictionary<string, string> { ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows) }, 120, 20));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var paste = terminal.WriteAsync(new string('x', 1024 * 1024));
        await Task.Delay(50);
        await Task.WhenAll(terminal.DisposeAsync().AsTask(), terminal.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await paste.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (IOException) { /* 关闭正在写入的管道应显式报告中断。 */ }
        await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        return "1 MiB paste and concurrent ConPTY shutdown completed";
    }

    private sealed class FakeTransport : IDeviceTransport
    {
        public Channel<ReadOnlyMemory<byte>> Packets { get; } = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public string Key => "responsiveness:fake-serial";
        public bool IsSimulated => true;
        public bool BlockSend
        {
            get; init;
        }
        public bool Closed
        {
            get; private set;
        }
        public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var packet in Packets.Reader.ReadAllAsync(cancellationToken))
            {
                yield return packet;
            }
        }
        public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (BlockSend)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
        }
        public ValueTask DisposeAsync()
        {
            Closed = true;
            Packets.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
