namespace StudioX.Devices;

using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

public sealed record SerialSettings(string PortName = "", int BaudRate = 115200, int DataBits = 8,
    Parity Parity = Parity.None, StopBits StopBits = StopBits.One, Handshake FlowControl = Handshake.None,
    bool Dtr = false, bool Rts = false)
{
    public void Validate()
    {
        if (!Regex.IsMatch(PortName, @"^COM[1-9]\d{0,4}$", RegexOptions.IgnoreCase)) throw new ArgumentException("请选择有效的 COM 串口。");
        if (BaudRate is < 50 or > 4000000) throw new ArgumentException("波特率范围为 50–4000000，实际支持取决于串口驱动。");
        if (DataBits is < 5 or > 8 || !Enum.IsDefined(Parity) || !Enum.IsDefined(StopBits) || StopBits == StopBits.None || !Enum.IsDefined(FlowControl))
            throw new ArgumentException("串口参数无效。");
        if (StopBits == StopBits.OnePointFive && DataBits != 5) throw new ArgumentException("1.5 停止位需要 5 数据位。");
        if (StopBits == StopBits.Two && DataBits == 5) throw new ArgumentException("5 数据位请使用 1 或 1.5 停止位。");
    }
    public bool HardwareFlow => FlowControl is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff;
}

public sealed record SerialPins(bool Cts, bool Dsr, bool CarrierDetect);

/// <summary>原始字节传输；编码与终端控制序列由上层处理。有限超时保证断开无需等待下一字节。</summary>
public sealed class SerialTransport(SerialSettings settings) : IDeviceTransport
{
    private SerialPort? port;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> errors = new();
    public string Key => "serial:" + settings.PortName.ToUpperInvariant();
    public bool IsSimulated => false;
    public static string[] GetPortNames() => SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ThenBy(p => p).ToArray();
    public async ValueTask OpenAsync(CancellationToken cancellationToken)
    {
        settings.Validate();
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var opened = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity, settings.DataBits, settings.StopBits)
            {
                Handshake = settings.FlowControl, DtrEnable = settings.Dtr,
                ReadTimeout = 100, WriteTimeout = 1000, ReadBufferSize = 65536,
                WriteBufferSize = 16384, DiscardNull = false, ParityReplace = 0
            };
            if (!settings.HardwareFlow) opened.RtsEnable = settings.Rts;
            opened.ErrorReceived += (sender, e) => { errors.Enqueue(e.EventType.ToString()); while (errors.Count > 100) errors.TryDequeue(out _); };
            try { opened.Open(); cancellationToken.ThrowIfCancellationRequested(); port = opened; }
            catch { opened.Dispose(); throw; }
        }, cancellationToken).ConfigureAwait(false);
    }
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var count = await Task.Run(() =>
            {
                try { return port!.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { return 0; }
            }, cancellationToken).ConfigureAwait(false);
            if (count > 0) yield return buffer.AsMemory(0, count).ToArray();
        }
    }
    public async ValueTask SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        var data = bytes.ToArray();
        await Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); port!.Write(data, 0, data.Length); }, cancellationToken).ConfigureAwait(false);
    }
    public SerialPins ReadPins() => new(port!.CtsHolding, port.DsrHolding, port.CDHolding);
    public void SetOutputs(bool dtr, bool rts)
    {
        port!.DtrEnable = dtr;
        if (!settings.HardwareFlow) port.RtsEnable = rts;
    }
    public string[] DrainErrors() { var result = new List<string>(); while (errors.TryDequeue(out var item)) result.Add(item); return result.ToArray(); }
    public ValueTask DisposeAsync() { port?.Dispose(); port = null; return ValueTask.CompletedTask; }
}
