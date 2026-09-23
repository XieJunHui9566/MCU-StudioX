namespace StudioX.Application.Serial;

using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StudioX.Extensions;
using StudioX.Extensions.Abstractions;

/// <summary>只观察原始收发，拥有独立有界队列；慢脚本不阻塞串口接收，也没有发送权限。</summary>
public sealed class SerialProtocolMonitor(string? hostExecutable = null) : IAsyncDisposable
{
    private sealed record Input(long Generation, SerialRecord Record);
    private readonly object sync = new();
    private readonly SemaphoreSlim configuration = new(1, 1);
    private readonly Queue<ProtocolFrame> frames = new();
    private Channel<Input>? channel;
    private CancellationTokenSource? lifetime;
    private Task worker = Task.CompletedTask;
    private long generation, version, sequence, discarded, dropped;
    private string message = "未启用协议解析";
    private bool failed, disposed;
    public ProtocolOptions Options { get; private set; } = new();
    public async Task ConfigureAsync(ProtocolOptions options)
    {
        if (!Enum.IsDefined(options.Protocol) || !Enum.IsDefined(options.Role) || options.IdleMilliseconds is < 20 or > 2000)
            throw new ArgumentException("空闲收尾时间应为 20–2000 ms。");
        await configuration.WaitAsync().ConfigureAwait(false);
        SerialScriptClient? script = null;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (options.Protocol == SerialProtocol.JavaScript)
            {
                if (string.IsNullOrWhiteSpace(options.ScriptPath)) throw new ArgumentException("请先选择 JavaScript 解析脚本。");
                var info = new FileInfo(options.ScriptPath);
                if (!info.Exists || info.Length > 262144) throw new ArgumentException("脚本文件不存在或超过 256 KiB。");
                var source = await File.ReadAllTextAsync(info.FullName).ConfigureAwait(false);
                if (source.Length > SerialScriptContract.MaxSource) throw new ArgumentException("脚本最多 65536 字符。");
                script = await SerialScriptClient.StartAsync(hostExecutable ?? Path.Combine(AppContext.BaseDirectory, "runtime", "plugin-host", "StudioX.PluginHost.exe"), source).ConfigureAwait(false);
            }
            await StopCoreAsync().ConfigureAwait(false);
            lock (sync)
            {
                Options = options; generation++; frames.Clear(); sequence = discarded = dropped = 0; failed = false; version++;
                message = options.Protocol switch { SerialProtocol.ModbusRtu => "Modbus RTU · 按长度与 CRC 重组；时间为宿主时间", SerialProtocol.JavaScript => "脚本：" + Path.GetFileName(options.ScriptPath), _ => "未启用协议解析" };
                if (options.Protocol == SerialProtocol.None) return;
                var newChannel = Channel.CreateBounded<Input>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
                channel = newChannel;
                lifetime = new();
                var ownedScript = script; script = null;
                var token = lifetime.Token;
                worker = Task.Run(() => RunAsync(newChannel, options, ownedScript, token));
            }
        }
        finally { if (script is not null) await script.DisposeAsync(); configuration.Release(); }
    }
    public void Observe(SerialRecord record)
    {
        lock (sync)
        {
            if (channel is null || failed) return;
            if (!channel.Writer.TryWrite(new(generation, record)))
            { dropped++; generation++; version++; message = "协议队列溢出，已丢弃记录并重置分帧；文字终端仍独立接收。"; }
        }
    }
    public void Clear()
    {
        lock (sync) { generation++; frames.Clear(); sequence = discarded = dropped = 0; version++; }
    }
    public ProtocolSnapshot? Snapshot(long previousVersion = -1)
    {
        lock (sync) return previousVersion == version ? null : new(version, frames.ToArray(), discarded, dropped, message, failed);
    }
    private void Add(ProtocolFrame frame, long current, bool offline)
    {
        lock (sync)
        {
            if (generation != current) return;
            frames.Enqueue(frame with { Sequence = ++sequence, Source = offline ? "离线样例（未发送）" : "串口" });
            while (frames.Count > 1000) { frames.Dequeue(); discarded++; }
            version++;
        }
    }
    private async Task RunAsync(Channel<Input> input, ProtocolOptions options, SerialScriptClient? script, CancellationToken token)
    {
        var modbus = new ModbusRtuDecoder(options.Role);
        List<byte>[] pending = [[], []]; DateTimeOffset[] first = [default, default], last = [default, default];
        bool[] samples = [false, false];
        var current = -1L;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (current != Interlocked.Read(ref generation))
                {
                    current = Interlocked.Read(ref generation); modbus.Reset(); foreach (var p in pending) p.Clear(); Array.Clear(last);
                    if (script is not null) await script.ResetAsync(token);
                }
                for (var processed = 0; processed < 256 && input.Reader.TryRead(out var item); processed++)
                {
                    if (item.Generation != current || current != Interlocked.Read(ref generation)) continue;
                    var record = item.Record; var side = record.Transmit ? 1 : 0;
                    if (record.Boundary)
                    {
                        await Flush(0); await Flush(1); modbus.Reset();
                        if (script is not null) await script.ResetAsync(token);
                        continue;
                    }
                    if (last[side] != default && (record.Offline != samples[side] || record.Time - last[side] >= TimeSpan.FromMilliseconds(options.IdleMilliseconds))) await Flush(side);
                    samples[side] = record.Offline;
                    last[side] = record.Time;
                    if (script is null) foreach (var f in modbus.Feed(record)) Add(f, current, samples[side]);
                    else
                    {
                        for (var i = 0; i < record.Data.Length; i += 1024)
                        {
                            if (pending[side].Count == 0) first[side] = record.Time;
                            pending[side].AddRange(record.Data.Skip(i).Take(1024));
                            if (pending[side].Count > SerialScriptContract.MaxInput) throw new InvalidDataException("脚本累计超过 8192 B 未完成分帧；请检查 consumed 返回值。");
                            await DecodeScript(side, false);
                        }
                    }
                }
                for (var side = 0; side < 2; side++)
                    if (last[side] != default && DateTimeOffset.UtcNow - last[side] >= TimeSpan.FromMilliseconds(options.IdleMilliseconds)) await Flush(side);
                await Task.Delay(25, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync) { failed = true; message = ex.ToString(); version++; }
        }
        finally { if (script is not null) await script.DisposeAsync(); }
        async Task Flush(int side)
        {
            if (script is null) foreach (var f in modbus.Feed(new(DateTimeOffset.UtcNow, side == 1, []), true)) Add(f, current, samples[side]);
            else if (pending[side].Count > 0)
            {
                await DecodeScript(side, true);
                if (pending[side].Count > 0)
                {
                    Add(new(first[side], side == 1 ? "TX" : "RX", "JavaScript", "—", "—", "不完整帧", "空闲收尾后仍有未消费字节",
                        Convert.ToHexString(pending[side].ToArray()), [new("说明", "脚本未完成分帧；保留原始数据供排查。")]), current, samples[side]);
                    pending[side].Clear();
                }
            }
            last[side] = default;
        }
        async Task DecodeScript(int side, bool flush)
        {
            if (pending[side].Count == 0) return;
            var bytes = pending[side].ToArray();
            var result = await script!.DecodeAsync(bytes, side == 1 ? "TX" : "RX", first[side], flush, token);
            foreach (var f in result.Frames)
                Add(new(first[side], side == 1 ? "TX" : "RX", "JavaScript", "—", "—", f.Status == "error" ? "脚本报告错误" : "已解析", f.Summary,
                    string.Join(' ', bytes.Skip(f.Offset).Take(f.Length).Select(b => b.ToString("X2"))),
                    (f.Fields ?? []).Select(p => new ProtocolField(p.Key, p.Value)).ToArray()), current, samples[side]);
            pending[side].RemoveRange(0, result.Consumed);
        }
    }
    public async Task ExportAsync(string path)
    {
        var snapshot = Snapshot()!;
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var frame in snapshot.Frames) await writer.WriteLineAsync(JsonSerializer.Serialize(new { clock = "host-utc", frame }, SerialScriptContract.Json));
    }
    private async Task StopCoreAsync()
    {
        lock (sync) { channel?.Writer.TryComplete(); channel = null; }
        if (lifetime is not null) await lifetime.CancelAsync();
        await worker; lifetime?.Dispose(); lifetime = null; worker = Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        await configuration.WaitAsync();
        try { disposed = true; await StopCoreAsync(); }
        finally { configuration.Release(); }
    }
}
