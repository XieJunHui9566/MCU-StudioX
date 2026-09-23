namespace StudioX.Application.SerialPlot;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using StudioX.Devices;

public sealed record PlotSample(double Seconds, double[] Values, long Segment);
public sealed record PlotSnapshot(long Version, bool Connected, bool Simulated, string Message, string? LastError,
    long ReceivedBytes, long ValidRecords, long InvalidRecords, long DroppedFrames, long EvictedRecords,
    int Channels, PlotFormat Format, PlotSample[] Samples);
public sealed record PlotPreferences(SerialSettings Connection, PlotFormat Format);

/// <summary>独立绘图会话。解析在后台完成，界面按固定频率取有界快照。</summary>
public sealed class SerialPlotService(DeviceHub hub, string dataDirectory,
    Func<SerialSettings, IDeviceTransport>? transportFactory = null) : IAsyncDisposable
{
    public const int HistoryCapacity = 20000;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object sync = new();
    private readonly Queue<PlotSample> history = new();
    private DeviceSession? session;
    private FrameSubscription? subscription;
    private CancellationTokenSource? lifetime;
    private Task consumer = Task.CompletedTask;
    private PlotFormat format = new();
    private PlotStreamParser parser = new(new());
    private bool connected, simulated, disposed;
    private string message = "未连接", lastError = "";
    private long version, received, valid, invalid, dropped, evicted, segment, recordIndex;
    private long? firstTimestamp;
    private long dropBaseline;
    public static IReadOnlyList<string> GetPorts() => SerialTransport.GetPortNames();

    public PlotSnapshot Snapshot()
    {
        lock (sync) return new(version, connected, simulated, message, lastError, received, valid, invalid, dropped,
            evicted, parser.Channels, format, history.ToArray());
    }

    public async Task ConnectAsync(SerialSettings settings, PlotFormat selectedFormat, bool demo = false)
    {
        selectedFormat.Validate();
        if (!demo) settings.Validate();
        await gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await DisconnectCoreAsync();
            var transport = demo ? new PlotDemoTransport(selectedFormat) :
                transportFactory?.Invoke(settings) ?? new SerialTransport(settings);
            var source = await hub.OpenAsync(transport);
            session = source;
            subscription = source.Subscribe(2048);
            lifetime = new();
            lock (sync)
            {
                format = selectedFormat; ResetCore(); simulated = demo; connected = true;
                // 实物串口可能从半行接入；先等一个结束符，不能让残缺首行锁定通道数。
                if (!demo) parser.LoseSynchronization();
                message = demo ? "演示数据 · 未连接硬件" : $"{settings.PortName} · {settings.BaudRate} baud";
            }
            var frames = subscription; var token = lifetime.Token;
            consumer = Task.Run(() => ConsumeAsync(source, frames, transport as SerialTransport, token));
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        await gate.WaitAsync();
        try { await DisconnectCoreAsync(); }
        finally { gate.Release(); }
    }
    private async Task DisconnectCoreAsync()
    {
        lifetime?.Cancel();
        await consumer;
        subscription?.Dispose(); subscription = null;
        if (session is not null) await session.DisposeAsync();
        session = null; lifetime?.Dispose(); lifetime = null;
        lock (sync) { connected = false; message = "已停止 · 保留采集数据"; version++; }
    }
    private async Task ConsumeAsync(DeviceSession source, FrameSubscription frames, SerialTransport? serial, CancellationToken token)
    {
        long previousSequence = -1;
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(token))
            {
                lock (sync)
                {
                    received += frame.Payload.Length;
                    var errors = serial?.DrainErrors() ?? [];
                    if (errors.Length > 0)
                    {
                        invalid++; segment++; parser.LoseSynchronization();
                        lastError = "串口驱动错误：" + string.Join(", ", errors);
                    }
                    var lost = frames.DroppedFrames - dropBaseline;
                    if (lost != dropped || frame.Sequence != previousSequence + 1)
                    {
                        dropped = lost; segment++; parser.LoseSynchronization();
                        lastError = "接收队列溢出，已断开曲线并等待下一组完整数据。";
                    }
                    previousSequence = frame.Sequence;
                    firstTimestamp ??= frame.Timestamp;
                    var elapsed = Math.Max(0, (frame.Timestamp - firstTimestamp.Value) / (double)Stopwatch.Frequency);
                    parser.Feed(frame.Payload.Span, (values, error) =>
                    {
                        var time = format.SampleIntervalMs > 0 ? recordIndex * format.SampleIntervalMs / 1000 : elapsed;
                        recordIndex++;
                        if (values is null) { invalid++; segment++; lastError = error ?? "数据格式错误。"; return; }
                        valid++;
                        history.Enqueue(new(time, values, segment));
                        if (history.Count > HistoryCapacity) { history.Dequeue(); evicted++; }
                    });
                    version++;
                }
            }
            lock (sync) { message = "数据源已结束"; connected = false; version++; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync) { connected = false; message = "连接中断"; lastError = ex.ToString(); version++; }
        }
        finally
        {
            // 拔线后也立即释放连接所有权，下一次连接无需关闭整个软件。
            await source.DisposeAsync();
        }
    }

    public void Clear()
    {
        lock (sync)
        {
            ResetCore();
            // 清空可能发生在一组中间，丢弃至下一边界，避免把后半个数字当作完整数据。
            if (connected) parser.LoseSynchronization();
        }
    }
    private void ResetCore()
    {
        history.Clear(); parser = new(format); firstTimestamp = null;
        received = valid = invalid = dropped = evicted = segment = recordIndex = 0;
        dropBaseline = subscription?.DroppedFrames ?? 0;
        lastError = ""; version++;
    }

    public static async Task ExportCsvAsync(string path, PlotSnapshot snapshot)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        await writer.WriteLineAsync("# MCU StudioX serial plot; " + (snapshot.Simulated ? "SIMULATED; " : "") +
            (snapshot.Format.SampleIntervalMs > 0 ? $"configured sample interval {snapshot.Format.SampleIntervalMs.ToString(CultureInfo.InvariantCulture)} ms" : "host monotonic receive time"));
        await writer.WriteLineAsync("time_s,segment," + string.Join(',', Enumerable.Range(1, snapshot.Channels).Select(i => "CH" + i)));
        foreach (var row in snapshot.Samples)
            await writer.WriteLineAsync(row.Seconds.ToString("R", CultureInfo.InvariantCulture) + "," + row.Segment + "," +
                string.Join(',', row.Values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
    }
    public async Task<PlotPreferences> LoadPreferencesAsync()
    {
        var path = Path.Combine(dataDirectory, "serial-plot.json");
        if (!File.Exists(path)) return new(new(), new());
        var preferences = JsonSerializer.Deserialize<PlotPreferences>(await File.ReadAllTextAsync(path)) ?? new(new(), new());
        preferences.Format.Validate();
        return preferences;
    }
    public async Task SavePreferencesAsync(PlotPreferences preferences)
    {
        preferences.Format.Validate(); Directory.CreateDirectory(dataDirectory);
        // 不持久化控制线的高电平；有些板把它们接到了 RESET / BOOT。
        var safe = preferences with { Connection = preferences.Connection with { Dtr = false, Rts = false } };
        var path = Path.Combine(dataDirectory, "serial-plot.json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(safe));
        File.Move(path + ".tmp", path, true);
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; await DisconnectCoreAsync(); }
        finally { gate.Release(); }
    }
}
