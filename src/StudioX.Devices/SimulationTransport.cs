namespace StudioX.Devices;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>可重复的模拟输入，时间戳代表宿主接收时间，不代表真实 IO 边沿精度。</summary>
public sealed class SimulationTransport(TimeSpan? interval = null) : IDeviceTransport
{
    private int open;
    public string Key => "simulation://sensor";
    public bool IsSimulated => true;
    public ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref open, 1) != 0) throw new InvalidOperationException("Transport already open.");
        return ValueTask.CompletedTask;
    }
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromMilliseconds(80));
        long step = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var value = 24 + Math.Sin(step * 0.08) * 4 + Math.Sin(step * 0.23) * 0.4;
            var line = string.Create(CultureInfo.InvariantCulture, $"{value:F3},{(step / 15) % 2}\n");
            yield return Encoding.UTF8.GetBytes(line);
            step++;
        }
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new NotSupportedException("模拟传感器是只读数据源。");
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref open, 0); return ValueTask.CompletedTask; }
}
