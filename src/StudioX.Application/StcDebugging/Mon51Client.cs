namespace StudioX.Application.StcDebugging;

using System.Text;
using StudioX.Devices;
using StudioX.Foundation;

/// <summary>Mon51 V2.5 请求/响应编解码；接收长度由当前请求确定，串口由 DeviceHub 独占。</summary>
public sealed class Mon51Client : IAsyncDisposable
{
    private readonly DeviceSession session;
    private readonly FrameSubscription subscription;
    private readonly List<byte> pending = [];
    private readonly StreamWriter log;
    private long receivedBytes;
    private bool synchronized;
    private bool disposed;

    public Mon51Client(DeviceSession session, string logPath)
        : this(session, logPath, "parameters not supplied") { }

    public Mon51Client(DeviceSession session, string logPath, string connectionParameters)
    {
        this.session = session;
        subscription = session.Subscribe(256);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        Record("OPEN", session.Key + " · " + connectionParameters + " · RX timestamps=DeviceFrame host UTC reception; other timestamps=host UTC processing · hardware=" + !session.IsSimulated);
    }

    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > 254)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }
        var result = new byte[payload.Length + 4];
        result[0] = 0xff;
        result[1] = 0x5a;
        result[2] = (byte)(payload.Length + 1);
        payload.CopyTo(result.AsSpan(3));
        result[^1] = unchecked((byte)-result.Take(result.Length - 1).Sum(b => b));
        return result;
    }

    public async Task<string> SynchronizeAsync(bool pause, CancellationToken token)
    {
        // 冷启动先按原厂顺序发 80/80/hello；尚未完成同步时的暂停帧会使本板后续握手无应答。
        // 已连接会话可直接暂停；首次附加正在运行的程序时，仅握手超时才退回暂停路径。
        if (pause && synchronized)
        {
            await SendAsync([0, 2], token);
            await SettleAsync(token);
        }
        await SendSynchronizationAsync(token);
        byte[] reply;
        try
        {
            reply = await BootAsync([0, 0], token);
        }
        catch (StudioXException error) when (pause && !synchronized && error.Code == "MON51_TIMEOUT")
        {
            Record("SYNC", "initial hello timed out; requesting pause at the same connection parameters");
            await SendAsync([0, 2], token);
            await SettleAsync(token);
            await SendSynchronizationAsync(token);
            reply = await BootAsync([0, 0], token);
        }
        var version = Encoding.ASCII.GetString(reply);
        if (version != "V2.5")
        {
            throw new StudioXException("MON51_VERSION", "监控程序版本未通过适配验证：" + Convert.ToHexString(reply) + "。当前支持 V2.5。");
        }
        synchronized = true;
        return version;
    }

    private async Task SendSynchronizationAsync(CancellationToken token)
    {
        for (var i = 0; i < 2; i++)
        {
            await SendRawAsync(new byte[] { 0x80 }, token);
            await SettleAsync(token);
        }
    }

    public async Task<byte[]> RegistersAsync(CancellationToken token)
    {
        await SettleAsync(token);
        await SendAsync([0, 1], token);
        return await ResponseAsync(19, token);
    }

    public async Task<byte[]> ReadAsync(Mon51MemorySpace space, ushort address, int count, CancellationToken token)
    {
        ValidateRange(space, address, count);
        await SettleAsync(token);
        await SendAsync([4, (byte)space, (byte)(address >> 8), (byte)address, (byte)count], token);
        return await ResponseAsync(count, token);
    }

    public static void ValidateRange(Mon51MemorySpace space, ushort address, int count)
    {
        var limit = space switch
        {
            Mon51MemorySpace.DataSfr or Mon51MemorySpace.Idata => 256,
            Mon51MemorySpace.Xdata => 1024,
            Mon51MemorySpace.Code => 0xdc00,
            _ => throw new ArgumentOutOfRangeException(nameof(space))
        };
        if (count is < 1 or > 128 || address + count > limit)
        {
            throw new StudioXException("MON51_RANGE", $"{space} 单次读取 1–128 字节，范围须在 0x0000–0x{limit - 1:X4} 内。");
        }
    }

    // CODE 只暴露单字节断点写入；擦除和固件下载继续由独立下载服务负责。
    public async Task WriteBreakpointByteAsync(ushort address, byte value, CancellationToken token)
    {
        if (address is < 3 or >= 0xdbfd)
        {
            throw new StudioXException("MON51_BREAKPOINT_RANGE", "断点须位于用户 CODE 0x0003–0xDBFC，避开复位向量和监控跳板。");
        }
        var ack = await BootAsync([2, 5, (byte)(address >> 8), (byte)address, 1, value], token);
        if (ack.Any(b => b != 0))
        {
            throw new StudioXException("MON51_ACK", "断点写入应答异常：" + Convert.ToHexString(ack));
        }
        if ((await ReadAsync(Mon51MemorySpace.Code, address, 1, token))[0] != value)
        {
            throw new StudioXException("MON51_VERIFY", $"断点字节 0x{address:X4} 回读不一致。");
        }
    }

    public Task<byte[]> StepAsync(CancellationToken token) => BootAsync([8, 1], token);
    internal async Task EraseUserProgramAsync(CancellationToken token)
    {
        // 实板用户区擦除约 2.5 秒，超过普通读取/控制命令的 2 秒应答期限。
        var ack = await BootAsync([6, 0, 0, 0, 0], token, TimeSpan.FromSeconds(8));
        if (ack.Any(value => value != 0))
        {
            throw new StudioXException("MON51_ACK", "用户程序擦除应答异常：" + Convert.ToHexString(ack));
        }
    }

    internal Task WriteUserProgramBlockAsync(ushort address, byte[] bytes, CancellationToken token)
    {
        if (bytes.Length is < 1 or > 40 || address == 0 && bytes.Length != 3 || address is 1 or 2 || address + bytes.Length > 0xdbfd)
        {
            throw new StudioXException("MON51_DOWNLOAD_RANGE", "用户程序块须避开监控及 DBFD 跳板；复位向量仅单独写入三字节。");
        }
        return WriteCoreAsync(5, address, bytes, token);
    }

    public Task<byte[]> RunAsync(CancellationToken token) => BootAsync([8, 0], token);
    public Task<byte[]> SetPcAsync(ushort address, CancellationToken token)
    {
        if (address >= 0xdbfd)
        {
            throw new StudioXException("MON51_PC_RANGE", "PC 必须位于用户 CODE 区；0x0000 表示逻辑复位入口。");
        }
        return BootAsync([2, 0, (byte)(address >> 8), (byte)address, 0], token);
    }

    public async Task WriteRamAsync(Mon51MemorySpace space, ushort address, byte[] bytes, CancellationToken token)
    {
        ValidateRange(space, address, bytes.Length);
        if (space == Mon51MemorySpace.Code || space is Mon51MemorySpace.Idata or Mon51MemorySpace.DataSfr && address + bytes.Length > 128)
        {
            throw new StudioXException("MON51_WRITE_RANGE", "内存编辑仅开放下 128 字节 RAM 和用户 XDATA；SFR/上半 IDATA 的写上下文映射尚未完整验证。");
        }
        await WriteCoreAsync((byte)space, address, bytes, token);
        if (!(await ReadAsync(space, address, bytes.Length, token)).SequenceEqual(bytes))
        {
            throw new StudioXException("MON51_VERIFY", "RAM 写入回读不一致。");
        }
    }

    // 官方 res10.dll 的 SetRegs 使用存储类型 4 写入保存的 SFR 上下文；不能替换为物理 SFR 写入。
    public Task WriteContextAsync(ushort address, byte value, CancellationToken token) => WriteCoreAsync(4, address, [value], token);

    private async Task WriteCoreAsync(byte space, ushort address, byte[] bytes, CancellationToken token)
    {
        var ack = await BootAsync([2, space, (byte)(address >> 8), (byte)address, (byte)bytes.Length, .. bytes], token);
        if (ack.Any(b => b != 0))
        {
            throw new StudioXException("MON51_ACK", "写入应答异常：" + Convert.ToHexString(ack));
        }
    }

    public bool TakeStopSignal()
    {
        Drain();
        var signaled = pending.Any(b => b is 0 or 0xff);
        if (pending.Count > 0)
        {
            Record("ASYNC", Convert.ToHexString(pending.ToArray()));
            pending.Clear();
        }
        return signaled;
    }

    private async Task<byte[]> BootAsync(byte[] payload, CancellationToken token, TimeSpan? responseTimeout = null)
    {
        await SettleAsync(token);
        await SendAsync(payload, token);
        return await ResponseAsync(null, token, responseTimeout);
    }

    private Task SendAsync(byte[] payload, CancellationToken token) => SendRawAsync(Encode(payload), token);
    private async Task SendRawAsync(byte[] bytes, CancellationToken token)
    {
        Record("TX", Convert.ToHexString(bytes));
        await session.SendAsync(bytes, token);
    }

    private void Drain()
    {
        while (subscription.Reader.TryRead(out var frame))
        {
            Record("RX", Convert.ToHexString(frame.Payload.Span), frame.ReceivedAt);
            receivedBytes += frame.Payload.Length;
            pending.AddRange(frame.Payload.ToArray());
        }
        if (subscription.DroppedFrames > 0 || pending.Count > 16384)
        {
            throw new StudioXException("MON51_OVERFLOW", $"串口接收溢出：丢失帧 {subscription.DroppedFrames}，缓冲 {pending.Count} 字节。详见原始日志。");
        }
        if (session.Failure is { } failure)
        {
            throw new StudioXException("MON51_TRANSPORT", "串口接收失败：" + failure.Message, failure);
        }
    }

    private async Task SettleAsync(CancellationToken token)
    {
        await Task.Delay(80, token);
        Drain();
        if (pending.Count > 0)
        {
            Record("PENDING", Convert.ToHexString(pending.ToArray()));
            pending.Clear();
        }
    }

    private async Task<byte[]> ResponseAsync(int? count, CancellationToken token, TimeSpan? timeout = null)
    {
        var before = receivedBytes;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            while (true)
            {
                Drain();
                var length = count is { } n ? n + 2 : 7;
                for (var offset = 0; offset + length <= pending.Count; offset++)
                {
                    var candidate = pending.GetRange(offset, length).ToArray();
                    var valid = count is not null
                        ? candidate[0] == 2 && candidate.Sum(b => b) % 256 == 0
                        : candidate[0] == 6 && candidate[1] == 0 && candidate[^1] == 4;
                    if (!valid)
                    {
                        continue;
                    }
                    pending.RemoveRange(0, offset + length);
                    Record("FRAME", Convert.ToHexString(candidate));
                    return count is not null ? candidate[1..^1] : candidate[2..6];
                }
                if (!await subscription.Reader.WaitToReadAsync(deadline.Token))
                {
                    throw new StudioXException("MON51_CLOSED", "串口接收已结束。");
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var received = receivedBytes - before;
            var remaining = Convert.ToHexString(pending.ToArray());
            Record("TIMEOUT", $"expected={(count is { } n ? n + 2 : 7)} · received={received} · pending={remaining}");
            var reason = received == 0 && pending.Count == 0
                ? "Mon51 未收到任何应答，请核对供电、串口、监控波特率、TX/RX 接线和芯片仿真配置。"
                : "Mon51 未收到完整有效应答，请核对监控版本与波特率；应答可能不完整或校验失败。";
            throw new StudioXException("MON51_TIMEOUT", reason + $" 连接={session.Key}；接收={received} 字节；剩余数据={remaining}。");
        }
    }

    private void Record(string kind, string value, DateTimeOffset? timestamp = null) => log.WriteLine($"{(timestamp ?? DateTimeOffset.UtcNow):O}\t{kind}\t{value}");
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        subscription.Dispose();
        try
        {
            await session.DisposeAsync();
        }
        finally { Record("CLOSE", session.Key); await log.DisposeAsync(); }
    }
}
