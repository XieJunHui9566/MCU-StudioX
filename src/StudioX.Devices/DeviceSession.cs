namespace StudioX.Devices;

using System.Diagnostics;

/// <summary>唯一连接所有者；日志、绘图和记录器只能通过订阅观察同一数据流。</summary>
public sealed class DeviceSession : IAsyncDisposable
{
    private readonly IDeviceTransport transport;
    private readonly Action<DeviceSession> released;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private readonly object sync = new();
    private readonly List<FrameSubscription> subscriptions = [];
    private Task pump = Task.CompletedTask;
    private bool started;
    private int disposed;
    private bool completed;
    private Exception? failure;
    internal DeviceSession(IDeviceTransport transport, Action<DeviceSession> released)
    {
        this.transport = transport; this.released = released;
    }
    public string Key => transport.Key;
    public bool IsSimulated => transport.IsSimulated;
    public Exception? Failure { get { lock (sync) return failure; } }
    public FrameSubscription Subscribe(int capacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            var subscription = new FrameSubscription(capacity, Remove);
            if (completed) subscription.Complete(failure); else subscriptions.Add(subscription);
            // 首个观察者就绪后才读取，避免串口启动时的首包落在订阅建立之前。
            if (!started) { started = true; pump = Task.Run(PumpAsync); }
            return subscription;
        }
    }
    private void Remove(FrameSubscription subscription) { lock (sync) subscriptions.Remove(subscription); }
    public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await sendGate.WaitAsync(linked.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            await transport.SendAsync(bytes, linked.Token);
        }
        finally { sendGate.Release(); }
    }
    private async Task PumpAsync()
    {
        long sequence = 0;
        Exception? error = null;
        try
        {
            await foreach (var bytes in transport.ReceiveAsync(lifetime.Token))
            {
                var frame = new DeviceFrame(sequence++, Stopwatch.GetTimestamp(), transport.IsSimulated, bytes);
                lock (sync) foreach (var subscriber in subscriptions) subscriber.Publish(frame);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            lock (sync)
            {
                completed = true; failure = error;
                foreach (var subscriber in subscriptions) subscriber.Complete(error);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync();
        await pump;
        await sendGate.WaitAsync();
        try { await transport.DisposeAsync(); }
        finally { sendGate.Release(); released(this); lifetime.Dispose(); }
    }
}
