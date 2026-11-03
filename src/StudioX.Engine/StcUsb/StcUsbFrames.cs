namespace StudioX.Engine.StcUsb;

using System.Buffers.Binary;
using StudioX.Ai8051Isp;
using StudioX.Foundation;

/// <summary>STC 原生 HID 的帧封装；型号、地址空间和硬件选项由各目标协议核对。</summary>
public sealed class StcUsbFrames(IHidReportTransport transport)
{
    public async Task ExpectAsync(byte[] payload, byte[] expected, int timeoutMs, CancellationToken token)
    {
        if (!(await ExchangeAsync(payload, timeoutMs, token)).AsSpan().SequenceEqual(expected))
        {
            throw Invalid($"USB 命令 0x{payload[0]:X2} 的结束应答不匹配；未继续操作。");
        }
    }

    public async Task<byte[]> ExchangeAsync(byte[] payload, int timeoutMs, CancellationToken token)
    {
        await SendAsync(payload, token);
        return DecodeReport(await transport.Read(timeoutMs, token));
    }

    public static byte[] DecodeReport(byte[] report)
    {
        if (report.Length != 65 || report[0] != 0 || report[1] != 0x46 || report[2] != 0xb9 || report[3] != 0x68)
        {
            throw Invalid("USB 应答报头或 Windows HID 报告长度无效。");
        }
        var size = BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(4));
        if (size is < 7 or > 62 || report[size + 2] != 0x16)
        {
            throw Invalid("USB 应答长度或结束标志无效。");
        }
        var checksum = 0;
        for (var index = 3; index < size; index++)
        {
            checksum += report[index];
        }
        if ((checksum & 0xffff) != BinaryPrimitives.ReadUInt16BigEndian(report.AsSpan(size)))
        {
            throw Invalid("USB 应答校验和错误。");
        }
        return report.AsSpan(6, size - 6).ToArray();
    }

    public async Task SendAsync(byte[] payload, CancellationToken token)
    {
        var frame = EncodeFrame(payload);
        for (var offset = 0; offset < frame.Length; offset += 64)
        {
            var report = new byte[65];
            frame.AsSpan(offset, Math.Min(64, frame.Length - offset)).CopyTo(report.AsSpan(1));
            await transport.Write(report, token);
        }
    }

    public static byte[] EncodeFrame(byte[] payload)
    {
        var frame = new byte[payload.Length + 8];
        frame[0] = 0x46;
        frame[1] = 0xb9;
        frame[2] = 0x6a;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3), checked((ushort)(payload.Length + 6)));
        payload.CopyTo(frame, 5);
        var checksum = frame.AsSpan(2, payload.Length + 3).ToArray().Sum(value => value);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(frame.Length - 3), (ushort)(checksum & 0xffff));
        frame[^1] = 0x16;
        return frame;
    }

    private static StudioXException Invalid(string message) => new("STC_USB_PROTOCOL", message);
}
