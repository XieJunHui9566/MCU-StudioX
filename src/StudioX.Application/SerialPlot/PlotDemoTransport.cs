namespace StudioX.Application.SerialPlot;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using StudioX.Devices;

/// <summary>明确标记的三通道演示，经过与真实串口相同的解码管线。</summary>
internal sealed class PlotDemoTransport(PlotFormat format) : IDeviceTransport
{
    public string Key => "simulation:serial-plot";
    public bool IsSimulated => true;
    public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var (field, record) = format.Validate();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        long index = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var t = index++ * .02;
            double[] values = [2048 + 1500 * Math.Sin(t * 2), 1024 + 700 * Math.Cos(t * 3.1), index % 150 < 5 ? 4095 : 256];
            yield return Encoding.UTF8.GetBytes(string.Join(field, values.Select(v => v.ToString("F3", CultureInfo.InvariantCulture))) + record);
        }
    }
    public ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
