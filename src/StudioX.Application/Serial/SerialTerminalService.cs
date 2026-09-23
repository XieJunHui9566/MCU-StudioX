namespace StudioX.Application.Serial;

using System.Text;
using System.Text.Json;
using StudioX.Devices;
using StudioX.Foundation;

public sealed record SerialRecord(DateTimeOffset Time, bool Transmit, byte[] Data, bool Boundary = false, bool Offline = false);
public sealed record SerialStatus(bool Connected, string Message, long Received, long Sent, long DroppedFrames,
    long DiscardedHistoryBytes, SerialPins? Pins, string[] Errors);
public sealed record SerialPreferences(SerialSettings Connection, SerialTextMode ReceiveMode = SerialTextMode.Utf8,
    SerialTextMode SendMode = SerialTextMode.Utf8, SerialLineEnding Ending = SerialLineEnding.CrLf, bool Ansi = true,
    bool Timestamps = false, bool Escapes = false, string[]? History = null);

/// <summary>串口连接、原始历史和显示状态的唯一所有者；UI 不直接访问 SerialPort。</summary>
public sealed class SerialTerminalService(DeviceHub hub, string dataDirectory, Func<SerialSettings, IDeviceTransport>? transportFactory = null, string? scriptHostExecutable = null) : IAsyncDisposable
{
    public SerialProtocolMonitor Protocol { get; } = new(scriptHostExecutable);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly AnsiTerminal terminal = new();
    private readonly Queue<SerialRecord> records = new();
    private readonly List<string> errors = [];
    private DeviceSession? session;
    private SerialTransport? transport;
    private FrameSubscription? subscription;
    private CancellationTokenSource? lifetime;
    private Task consumer = Task.CompletedTask;
    private SerialTextMode mode;
    private Decoder decoder = SerialCodec.EncodingFor(SerialTextMode.Utf8).GetDecoder();
    private bool ansi = true, echo, connected, disposed;
    private string message = "未连接";
    private long received, sent, dropped, discarded;
    private int historyBytes;
    private SerialPins? pins;
    public static Task<string[]> ListPortsAsync() => Task.Run(SerialTransport.GetPortNames);
    public async Task<SerialPreferences> LoadPreferencesAsync()
    {
        var path = Path.Combine(dataDirectory, "serial.json");
        return File.Exists(path) ? await JsonStore.ReadAsync<SerialPreferences>(path).ConfigureAwait(false) : new(new());
    }
    public Task SavePreferencesAsync(SerialPreferences preferences) => JsonStore.WriteAsync(Path.Combine(dataDirectory, "serial.json"),
        preferences with { Connection = preferences.Connection with { Dtr = false, Rts = false }, History = preferences.History?.Take(30).ToArray() });
    public async Task ConnectAsync(SerialSettings settings)
    {
        settings.Validate(); await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await DisconnectCoreAsync().ConfigureAwait(false);
            var next = transportFactory?.Invoke(settings) ?? new SerialTransport(settings);
            var opened = await hub.OpenAsync(next).ConfigureAwait(false);
            session = opened; transport = next as SerialTransport; subscription = opened.Subscribe(2048); lifetime = new();
            lock (sync)
            {
                decoder.Reset(); terminal.ResetControl(); connected = true; pins = null; dropped = 0;
                message = $"已连接 {settings.PortName} · {settings.BaudRate} / {settings.DataBits} / {settings.Parity} / {settings.StopBits}";
                Protocol.Observe(new(DateTimeOffset.UtcNow, false, [], true));
            }
            consumer = ConsumeAsync(opened, subscription, lifetime.Token);
        }
        finally { gate.Release(); }
    }
    private async Task ConsumeAsync(DeviceSession source, FrameSubscription frames, CancellationToken token)
    {
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                lock (sync)
                {
                    var droppedNow = frames.DroppedFrames;
                    if (droppedNow > dropped) { decoder.Reset(); terminal.ResetControl(); Protocol.Observe(new(frame.ReceivedAt, false, [], true)); errors.Add($"接收队列丢失 {droppedNow - dropped} 帧，已重置文本与协议解码器。"); dropped = droppedNow; }
                    received += frame.Payload.Length;
                    var record = new SerialRecord(frame.ReceivedAt, false, frame.Payload.ToArray());
                    Remember(record); Display(record);
                }
            }
            if (!token.IsCancellationRequested) lock (sync) { connected = false; message = "串口已断开"; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) lock (sync) { connected = false; message = "串口连接中断：" + ex.GetBaseException().Message; errors.Add(ex.ToString()); }
        }
        finally
        {
            // 设备拔出时及时释放句柄，不让失效会话占住 COM 端口。
            if (!token.IsCancellationRequested) await source.DisposeAsync().ConfigureAwait(false);
        }
    }
    private void Remember(SerialRecord record)
    {
        Protocol.Observe(record);
        records.Enqueue(record); historyBytes += record.Data.Length;
        while (historyBytes > 2 * 1024 * 1024 || records.Count > 16384)
        { var removed = records.Dequeue(); historyBytes -= removed.Data.Length; discarded += removed.Data.Length; }
        if (errors.Count > 100) errors.RemoveRange(0, errors.Count - 100);
    }
    private void Display(SerialRecord record)
    {
        if (record.Boundary)
        {
            if (mode != SerialTextMode.Hex)
            {
                var tail = new char[8]; var count = decoder.GetChars([], 0, 0, tail, 0, true);
                if (count > 0) terminal.Feed(new string(tail, 0, count), record.Time, ansi);
            }
            decoder.Reset(); terminal.ResetControl(); return;
        }
        if (record.Transmit)
        {
            if (echo) terminal.Echo(mode == SerialTextMode.Hex ? Convert.ToHexString(record.Data.AsSpan()) : SerialCodec.EncodingFor(mode).GetString(record.Data), record.Time);
            return;
        }
        if (mode == SerialTextMode.Hex)
        {
            for (var i = 0; i < record.Data.Length; i += 16)
                terminal.Feed(string.Join(' ', record.Data.Skip(i).Take(16).Select(b => b.ToString("X2"))) + "\r\n", record.Time, false);
        }
        else
        {
            var buffer = new char[SerialCodec.EncodingFor(mode).GetMaxCharCount(record.Data.Length)];
            var count = decoder.GetChars(record.Data, 0, record.Data.Length, buffer, 0, false);
            terminal.Feed(new string(buffer, 0, count), record.Time, ansi);
        }
    }
    public async Task SetDisplayAsync(SerialTextMode newMode, bool color, bool showTransmit)
    {
        await Task.Run(() =>
        {
            lock (sync)
            {
                mode = newMode; ansi = color; echo = showTransmit;
                decoder = SerialCodec.EncodingFor(mode == SerialTextMode.Hex ? SerialTextMode.Utf8 : mode).GetDecoder();
                terminal.Clear(); foreach (var record in records) Display(record);
            }
        }).ConfigureAwait(false);
    }
    public TerminalSnapshot? ReadDisplay(long previousVersion, bool timestamps)
    { lock (sync) return terminal.Version == previousVersion ? null : terminal.Snapshot(timestamps); }
    public SerialStatus Status { get { lock (sync) return new(connected, message, received, sent, dropped, discarded, pins, errors.ToArray()); } }
    public async Task SendAsync(byte[] bytes)
    {
        if (bytes.Length is 0 or > 65536) throw new ArgumentException("发送内容不能为空且不能超过 64 KiB。");
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (sync) if (!connected) throw new InvalidOperationException("请先连接串口。");
            try { await session!.SendAsync(bytes).ConfigureAwait(false); }
            catch (TimeoutException ex) { throw new IOException("发送超时，可能受到 CTS/XOFF 流控阻塞；部分字节可能已发送，请勿盲目重发。", ex); }
            lock (sync)
            {
                sent += bytes.Length; var record = new SerialRecord(DateTimeOffset.UtcNow, true, bytes.ToArray());
                Remember(record); Display(record);
            }
        }
        finally { gate.Release(); }
    }
    public async Task PollPinsAsync()
    {
        if (!await gate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (transport is null || !Status.Connected) return;
            try
            {
                var value = await Task.Run(transport.ReadPins).ConfigureAwait(false);
                var failures = transport.DrainErrors();
                lock (sync) { pins = value; errors.AddRange(failures.Select(f => "串口驱动报告：" + f)); if (errors.Count > 100) errors.RemoveRange(0, errors.Count - 100); }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { lock (sync) { pins = null; message = "串口状态读取失败：" + ex.Message; } }
        }
        finally { gate.Release(); }
    }
    public async Task SetOutputsAsync(bool dtr, bool rts)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (transport is not null && Status.Connected) await Task.Run(() => transport.SetOutputs(dtr, rts)).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public void Clear()
    {
        lock (sync) { terminal.Clear(); decoder.Reset(); records.Clear(); historyBytes = 0; discarded = 0; Protocol.Clear(); }
    }
    public void ResetCounters() { lock (sync) { received = sent = 0; } }
    public void AnalyzeOffline(byte[] bytes, bool transmit = false)
    {
        if (bytes.Length is 0 or > 65536) throw new ArgumentException("离线输入应为 1–65536 B。");
        lock (sync)
        {
            if (connected) throw new InvalidOperationException("请先断开串口，再进行离线解析。");
            if (Protocol.Options.Protocol == SerialProtocol.None) throw new InvalidOperationException("请先在协议解析页选择并应用解析器。");
            if (Protocol.Snapshot()!.Failed) throw new InvalidOperationException("解析器已停止，请点击应用 / 重载。");
            Protocol.Observe(new(DateTimeOffset.UtcNow, transmit, bytes.ToArray(), Offline: true));
        }
    }
    public async Task ExportAsync(string path)
    {
        SerialRecord[] copy; string text;
        lock (sync) { copy = records.Select(r => r with { Data = r.Data.ToArray() }).ToArray(); text = terminal.Snapshot(true).Text; }
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".bin")
        {
            await using var file = File.Create(path);
            foreach (var record in copy.Where(r => !r.Transmit)) await file.WriteAsync(record.Data).ConfigureAwait(false);
        }
        else if (extension == ".jsonl")
        {
            await using var file = new StreamWriter(path, false, new UTF8Encoding(false));
            foreach (var record in copy) await file.WriteLineAsync(JsonSerializer.Serialize(new { timestamp = record.Time, clock = "host-utc", direction = record.Boundary ? "boundary" : record.Transmit ? "TX" : "RX", dataBase64 = Convert.ToBase64String(record.Data) })).ConfigureAwait(false);
        }
        else await File.WriteAllTextAsync(path, text, new UTF8Encoding(false)).ConfigureAwait(false);
    }
    public async Task DisconnectAsync()
    { await gate.WaitAsync().ConfigureAwait(false); try { await DisconnectCoreAsync().ConfigureAwait(false); } finally { gate.Release(); } }
    private async Task DisconnectCoreAsync()
    {
        var hadSession = session is not null;
        if (lifetime is not null) await lifetime.CancelAsync().ConfigureAwait(false);
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        await consumer.ConfigureAwait(false); subscription?.Dispose(); lifetime?.Dispose();
        session = null; subscription = null; transport = null; lifetime = null; consumer = Task.CompletedTask;
        lock (sync)
        {
            if (hadSession) { var boundary = new SerialRecord(DateTimeOffset.UtcNow, false, [], true); Remember(boundary); Display(boundary); }
            connected = false; pins = null; message = "已断开";
        }
    }
    public async ValueTask DisposeAsync()
    { await gate.WaitAsync().ConfigureAwait(false); try { disposed = true; await DisconnectCoreAsync().ConfigureAwait(false); await Protocol.DisposeAsync().ConfigureAwait(false); } finally { gate.Release(); } }
}
