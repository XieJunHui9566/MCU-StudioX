namespace StudioX.Application.OpenOcdPlot;

using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>主机定时只读采样；调试服务仍是探针、ELF 和目标状态的唯一所有者。</summary>
public sealed class OpenOcdPlotService(IOpenOcdPlotSource source, int capacity = 10000) : IAsyncDisposable
{
    public const int Capacity = 10000;
    private readonly int retainedCapacity = capacity is >= 16 and <= Capacity ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly object sync = new();
    private readonly List<OpenOcdPlotChannel> channels = [];
    private readonly Queue<OpenOcdPlotRecord> records = new();
    private CancellationTokenSource? cancellation;
    private Task worker = Task.CompletedTask;
    private bool capturing, disposed, sessionEnded;
    private string status = "先启动实机调试，暂停时添加全局变量，再开始采集。";
    private string target = "", error = "";
    private long missed, evicted, invalid, segment;
    private int interval = 100;

    public OpenOcdPlotSnapshot Snapshot()
    {
        lock (sync) { return new(capturing, status, error.Length == 0 ? null : error, target, channels.ToArray(),
            records.Select(row => row with { Values = row.Values.ToArray() }).ToArray(), missed, evicted, invalid, interval); }
    }

    public async Task AddAsync(string expression, PlotScalar type, CancellationToken token = default)
    {
        await operations.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lock (sync)
            {
                RequireEditable();
                if (channels.Count >= 8) { throw new InvalidOperationException("最多同时绘制 8 个变量。"); }
                if (channels.Any(c => c.Expression == expression.Trim())) { throw new InvalidOperationException("变量已添加。"); }
                if (channels.Any(c => c.SessionId != source.PlotSessionId)) { throw new InvalidOperationException("调试会话已更换，请先清空通道并重新添加变量。"); }
            }
            var channel = await source.ResolvePlotChannelAsync(expression.Trim(), type, token);
            lock (sync)
            {
                channels.Add(channel);
                target = source.PlotTarget;
                ClearRecords();
                status = "变量已解析。开始采集后，使用调试工具栏的运行按钮让目标继续执行。";
            }
        }
        finally { operations.Release(); }
    }

    public void Remove(int index)
    {
        lock (sync)
        {
            RequireEditable();
            if (index < 0 || index >= channels.Count) { return; }
            channels.RemoveAt(index);
            ClearRecords();
        }
    }

    public void Clear(bool removeChannels)
    {
        lock (sync)
        {
            RequireEditable();
            if (removeChannels) { channels.Clear(); }
            ClearRecords();
            status = removeChannels ? "通道已清空，请在暂停时添加变量。" : "曲线已清空，可以重新开始采集。";
        }
    }

    private void RequireEditable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (capturing) { throw new InvalidOperationException("请先停止采集，再修改通道或清空数据。"); }
    }
    private void ClearRecords() { records.Clear(); missed = evicted = invalid = segment = 0; error = ""; }

    public async Task StartAsync(int intervalMs)
    {
        if (intervalMs is < 20 or > 10000) { throw new ArgumentOutOfRangeException(nameof(intervalMs), "采样间隔应为 20–10000 ms。"); }
        await operations.WaitAsync();
        try
        {
            lock (sync)
            {
                RequireEditable();
                if (!source.CanPlot || channels.Count == 0 || channels.Any(c => c.SessionId != source.PlotSessionId))
                { throw new InvalidOperationException("需要当前实机调试会话中的变量；重新连接后请清空通道并重新添加。"); }
                cancellation?.Dispose();
                cancellation = new();
                ClearRecords();
                interval = intervalMs;
                sessionEnded = false;
                capturing = true;
                status = "采集中 · 时间为主机读回时间";
                var selected = channels.ToArray();
                worker = Task.Run(() => CaptureAsync(selected, intervalMs, cancellation.Token));
            }
        }
        finally { operations.Release(); }
    }

    private async Task CaptureAsync(OpenOcdPlotChannel[] selected, int periodMs, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var next = 0d;
        bool? wasRunning = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                // 用户停止仅取消下一轮；已发出的 RPC 等待有界响应，避免取消污染 Tcl 帧序。
                var reading = await source.ReadPlotAsync(selected);
                var elapsed = clock.Elapsed.TotalMilliseconds;
                lock (sync)
                {
                    if (token.IsCancellationRequested) { break; }
                    if (wasRunning is not null && wasRunning != reading.TargetRunning) { segment++; }
                    wasRunning = reading.TargetRunning;
                    if (reading.Values.Length != selected.Length) { throw new IOException("采样通道数量不匹配。"); }
                    if (reading.Values.Any(value => !double.IsFinite(value))) { invalid++; segment++; }
                    else
                    {
                        if (records.Count == retainedCapacity) { records.Dequeue(); evicted++; }
                        records.Enqueue(new(elapsed / 1000, reading.HostTime, reading.TargetRunning, reading.Values.ToArray(), segment));
                    }
                    status = reading.TargetRunning ? "采集中 · 目标运行中" : "采集中 · 目标已暂停（静态值）";
                    next += periodMs;
                    if (next < elapsed)
                    {
                        var skipped = (long)Math.Floor((elapsed - next) / periodMs) + 1;
                        missed += skipped;
                        segment++;
                        next += skipped * periodMs;
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, next - clock.Elapsed.TotalMilliseconds)), token);
            }
            lock (sync) { status = "已停止采集 · 目标运行状态未改变"; }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { lock (sync) { status = "已停止采集 · 目标运行状态未改变"; } }
        catch (Exception ex)
        {
            lock (sync) { error = ex.ToString(); status = "采集已停止：" + ex.Message; }
        }
        finally
        {
            lock (sync)
            {
                capturing = false;
                if (sessionEnded) { status = "调试会话已结束，采集已停止。重新连接后请清空通道并重新添加变量。"; }
            }
        }
    }

    public void StopIfSessionEnded()
    {
        lock (sync)
        {
            if (capturing && (!source.CanPlot || channels.Any(channel => channel.SessionId != source.PlotSessionId)))
            {
                sessionEnded = true;
                cancellation?.Cancel();
            }
        }
    }

    public async Task StopAsync()
    {
        await operations.WaitAsync();
        try { cancellation?.Cancel(); await worker; }
        finally { operations.Release(); }
    }

    public string ExportCsv()
    {
        var snapshot = Snapshot();
        static string Q(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var text = new StringBuilder("# OpenOCD plot; timestamp=host read completion; not MCU sampling time\r\n");
        text.AppendLine("# target=" + snapshot.Target.Replace('\r', ' ').Replace('\n', ' '));
        text.AppendLine(FormattableString.Invariant($"# interval_ms={snapshot.IntervalMs}; missed_intervals={snapshot.MissedIntervals}; evicted_records={snapshot.EvictedRecords}; invalid_records={snapshot.InvalidRecords}"));
        if (snapshot.Error is not null) { text.AppendLine("# error=" + snapshot.Status.Replace('\r', ' ').Replace('\n', ' ')); }
        text.Append("host_time,elapsed_seconds,target_running,segment");
        foreach (var channel in snapshot.Channels) { text.Append(',').Append(Q(channel.Expression + "@" + channel.AddressText + ":" + channel.Type)); }
        text.AppendLine();
        foreach (var record in snapshot.Records)
        {
            text.Append(record.HostTime.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                .Append(record.Seconds.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(record.TargetRunning ? "true" : "false").Append(',').Append(record.Segment);
            foreach (var value in record.Values) { text.Append(',').Append(value.ToString("R", CultureInfo.InvariantCulture)); }
            text.AppendLine();
        }
        return text.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await operations.WaitAsync();
        try
        {
            lock (sync)
            {
                if (disposed) { return; }
                disposed = true;
                cancellation?.Cancel();
            }
            await worker;
            cancellation?.Dispose();
        }
        finally { operations.Release(); }
    }
}
