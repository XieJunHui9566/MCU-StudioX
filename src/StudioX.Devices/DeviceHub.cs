namespace StudioX.Devices;

using StudioX.Foundation;

public sealed class DeviceHub : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, DeviceSession> sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> reservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object sync = new();
    private bool disposed;
    public async Task<DeviceSession> OpenAsync(IDeviceTransport transport, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (sessions.ContainsKey(transport.Key) || reservations.Contains(transport.Key))
                {
                    throw new StudioXException("DEVICE_OWNED", $"连接 {transport.Key} 已由一个会话占用。");
                }
            }
            try
            {
                await transport.OpenAsync(cancellationToken);
            }
            catch { await transport.DisposeAsync(); throw; }
            var session = new DeviceSession(transport, Release);
            lock (sync)
            {
                sessions.Add(transport.Key, session);
            }
            return session;
        }
        finally { gate.Release(); }
    }
    /// <summary>阻止串口采集与外部下载进程同时取得同一连接；调用方必须在工具退出后释放。</summary>
    public async Task<DeviceConnectionReservation> ReserveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (sessions.ContainsKey(key) || !reservations.Add(key))
                {
                    throw new StudioXException("DEVICE_OWNED", $"连接 {key} 已占用；请先停止该端口的串口采集或下载。");
                }
            }
            return new(() => { lock (sync) { reservations.Remove(key); } });
        }
        finally { gate.Release(); }
    }
    private void Release(DeviceSession session)
    {
        lock (sync)
        {
            if (sessions.TryGetValue(session.Key, out var existing) && ReferenceEquals(existing, session))
            {
                sessions.Remove(session.Key);
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        DeviceSession[] current;
        try
        {
            lock (sync)
            {
                disposed = true;
                current = sessions.Values.ToArray();
            }
        }
        finally { gate.Release(); }
        foreach (var session in current)
        {
            await session.DisposeAsync();
        }
    }
}
