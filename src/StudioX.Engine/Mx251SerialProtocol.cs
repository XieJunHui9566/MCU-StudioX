namespace StudioX.Engine;

using System.Buffers.Binary;
using StudioX.Ai8051Isp;
using StudioX.Engine.StcUsb;
using StudioX.Foundation;

/// <summary>AI8051U 串口 ISP：2400 8E1 握手/计频，115200 8E1 写入，64 字节块。</summary>
public sealed class Mx251SerialProtocol(IMx251SerialTransport transport, Action<object> progress)
{
    private readonly List<byte> received = [];
    private TargetInfo? target;
    private bool started;
    private byte[]? TakeReply()
    {
        while (received.Count > 0 && (received[0] != 0x46 || received.Count >= 2 && received[1] != 0xb9 || received.Count >= 3 && received[2] != 0x68)) received.RemoveAt(0);
        if (received.Count < 5) return null;
        var size = (received[3] << 8) | received[4];
        if (size is < 7 or > 62) throw Invalid("串口 ISP 应答长度无效。");
        if (received.Count < size + 2) return null;
        var report = new byte[65]; received.GetRange(0, size + 2).CopyTo(report, 1); received.RemoveRange(0, size + 2);
        return StcUsbFrames.DecodeReport(report);
    }
    private async Task<byte[]> ReadReplyAsync(int timeout, CancellationToken token, byte? pulse = null)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeout)
        {
            token.ThrowIfCancellationRequested();
            if (TakeReply() is { } ready) return ready;
            if (pulse is { } value && received.Count == 0) await transport.WriteAsync([value], token);
            received.AddRange(await transport.ReadAsync(pulse.HasValue ? 20 : 100, token));
        }
        throw new TimeoutException("等待 AI8051U 串口 ISP 应答超时。");
    }
    private async Task<byte[]> ExchangeAsync(byte[] packet, byte[]? expected, int timeout, CancellationToken token, byte? pulse = null)
    {
        await transport.WriteAsync(StcUsbFrames.EncodeFrame(packet), token);
        var reply = await ReadReplyAsync(timeout, token, pulse);
        if (expected is not null && !reply.AsSpan().SequenceEqual(expected)) throw Invalid($"串口命令 {packet[0]:X2} 应答不符：{Convert.ToHexString(reply)}。");
        return reply;
    }
    public async Task<TargetInfo> IdentifyAsync(CancellationToken token)
    {
        if (started) throw Invalid("下载会话不能自动重试。");
        target = null;
        transport.SetBaudRate(2400);
        progress(new { stage = "waiting", message = "COM 串口已打开，请将 AI8051U 断电再上电进入 ISP；可点击停止取消。" });
        var status = await ReadReplyAsync(60000, token, 0x7f);
        if (status.Length != 54 || status[0] != 0x50 || status[20] != 0x78 || status[21] != 0xb4 ||
            status[17] != 0x74 || status[18] != 0x55 || status[22] != 0x11)
            throw Invalid("串口实机与 AI8051U-34K64 / BSL 7.4.1U 不符；未擦写。");
        target = new("AI8051U-34K64", "7.4.1U", "78B4", BinaryPrimitives.ReadUInt32BigEndian(status.AsSpan(1)), Convert.ToHexString(status));
        DeviceConfiguration.CheckExisting(target);
        progress(new { stage = "detected", target, transport = "serial" });
        return target;
    }
    public async Task<IspSessionResult> ProgramAsync(byte[] image, Mx251IspSettings settings, CancellationToken token)
    {
        settings.Validate(requireDevice: true);
        if (started || target is null || image.Length is < 1 or > 65536 || settings.DownloadMode != Mx251DownloadMode.Serial)
            throw Invalid("串口下载状态、固件范围或通道无效。");
        started = true;
        var calibration = await IrcCalibration.MeasureAsync(new(1, "AI8051U-34K64", 32, settings.IrcHz), async (packet, prefix, ct) =>
        {
            var reply = await ExchangeAsync(packet, null, 3000, ct, 0x66);
            if (!reply.AsSpan().StartsWith(prefix)) throw Invalid("IRC 计频应答不符。");
            return reply;
        }, progress, token);
        var options = DeviceConfiguration.BuildOptions(calibration.MeasuredHz, calibration.Trim, calibration.Range, calibration.Divider, calibration.RequestedHz);
        // 已捕获的编程频率固定 24 MHz，FFCC 对应 115200；用户 IRC 在配置阶段写入。
        await ExchangeAsync([1, 0, 0, 0xff, 0xcc, calibration.Range, calibration.Trim, 0x98], [1], 3000, token);
        transport.SetBaudRate(115200);
        await Task.Delay(30, token);
        await ExchangeAsync([5, 0, 0, 0x5a, 0xa5], [5], 3000, token);
        progress(new { stage = "erase-start", flashReadback = false });
        var erase = await ExchangeAsync([3, 0, 0, 0x5a, 0xa5], null, 10000, token);
        if (erase.Length != 8 || erase[0] != 3 || erase[1] != 0x78 || erase[2] != 0xb4) throw Invalid("串口擦除应答型号或长度不符。");
        var padded = (image.Length + 63) / 64 * 64;
        for (var address = 0; address < padded; address += 64)
        {
            var packet = new byte[69]; packet.AsSpan().Fill(0xff);
            packet[0] = address == 0 ? (byte)0x22 : (byte)2;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), checked((ushort)address));
            packet[3] = 0x5a; packet[4] = 0xa5;
            image.AsSpan(address, Math.Min(64, image.Length - address)).CopyTo(packet.AsSpan(5));
            await ExchangeAsync(packet, [2, 0x54], 5000, token);
            progress(new { stage = "program", acknowledgedBytes = address + 64, totalBytes = padded });
            await Task.Delay(20, token);
        }
        await Task.Delay(60, token);
        await ExchangeAsync(options, [4, 0x54], 5000, token);
        progress(new { stage = "configuration-acknowledged", calibration, configurationReadback = false });
        await transport.WriteAsync(StcUsbFrames.EncodeFrame([0xff]), token);
        progress(new { stage = "run-command-sent", acknowledged = false });
        // ACK 不等于重新进入 ISP 回读；保留原始身份，报告单独记录实际写入频率。
        return new(target, true, false);
    }
    private static StudioXException Invalid(string message) => new("MX251_SERIAL", message);
}

