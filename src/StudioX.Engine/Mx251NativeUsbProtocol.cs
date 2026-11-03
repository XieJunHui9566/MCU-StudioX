namespace StudioX.Engine;

using System.Buffers.Binary;
using StudioX.Ai8051Isp;
using StudioX.Engine.StcUsb;
using StudioX.Foundation;

/// <summary>AI8051U 原生 USB ISP；分包、型号和选项布局来自 2026-11-03 官方成功下载记录。</summary>
public sealed class Mx251NativeUsbProtocol(IHidReportTransport transport, Action<object> progress)
{
    private readonly StcUsbFrames frames = new(transport);
    private TargetInfo? target;
    private bool started;
    public async Task<TargetInfo> IdentifyAsync(CancellationToken token)
    {
        if (started)
        {
            throw Invalid("已开始的下载会话不能重新握手或自动重试。");
        }
        target = null;
        var status = await frames.ExchangeAsync([0], 3000, token);
        if (status.Length != 54 || status[0] != 0 || status[20] != 0x78 || status[21] != 0xb4 ||
            status[17] != 0x74 || status[18] != 0x55 || status[22] != 0x11)
        {
            throw Invalid("原生 USB 实机与 AI8051U-34K64 / BSL 7.4.1U 不符；未擦写。");
        }
        target = new("AI8051U-34K64", "7.4.1U", "78B4",
            BinaryPrimitives.ReadUInt32BigEndian(status.AsSpan(1)), Convert.ToHexString(status));
        progress(new
        {
            stage = "detected",
            target,
            transport = "native-usb"
        });
        return target;
    }
    public async Task<IspSessionResult> ProgramAsync(byte[] image, Mx251IspSettings settings, CancellationToken token)
    {
        if (started || target is null || image.Length is < 1 or > 65536)
        {
            throw Invalid("须先核对型号，固件须在 FF0000–FFFFFF 的 64 KiB 代码区内。");
        }
        settings.Validate();
        if (settings.DownloadMode != Mx251DownloadMode.NativeUsb)
        {
            throw Invalid("下载通道不一致。");
        }
        var options = CreateOptions(target, settings.IrcHz);
        started = true;
        await frames.ExpectAsync([1, 0, 0, 0, 0, 0, 0, 0x80], [1], 3000, token);
        await frames.ExpectAsync([5, 0, 0, 0x5a, 0xa5], [5], 3000, token);
        progress(new
        {
            stage = "erase-start",
            flashReadback = false
        });
        var erased = await frames.ExchangeAsync([3, 0, 0, 0x5a, 0xa5], 10000, token);
        if (erased.Length != 8 || erased[0] != 3 || erased[1] != 0x78 || erased[2] != 0xb4)
        {
            throw Invalid("USB 擦除应答的长度或型号不符；未继续写入。");
        }
        int padded = (image.Length + 127) / 128 * 128;
        for (int address = 0; address < padded; address += 128)
        {
            var packet = new byte[133];
            packet.AsSpan().Fill(0xff);
            packet[0] = address == 0 ? (byte)0x32 : (byte)0x12;
            // 协议地址是 FF0000 代码窗口内的偏移，不是 HEX 的完整地址。
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), checked((ushort)address));
            packet[3] = 0x5a;
            packet[4] = 0xa5;
            image.AsSpan(address, Math.Min(128, image.Length - address)).CopyTo(packet.AsSpan(5));
            await frames.ExpectAsync(packet, [2, 0x54], 5000, token);
            progress(new
            {
                stage = "program",
                acknowledgedBytes = address + 128,
                totalBytes = padded
            });
        }
        await frames.ExpectAsync(options, [4, 0x54], 5000, token);
        progress(new
        {
            stage = "configuration-acknowledged",
            cpuMode = 32,
            ircHz = target.IrcHz,
            configurationReadback = false
        });
        await frames.SendAsync([0xff], token);
        progress(new
        {
            stage = "run-command-sent",
            acknowledged = false
        });
        return new(target, true, false);
    }
    public static byte[] CreateOptions(TargetInfo target, int requestedHz)
    {
        var status = Convert.FromHexString(target.RawStatus);
        if (status.Length != 54 || status[7] is < 1 or > 8 || status[6] is not (0 or 0x10 or 0x20 or 0x30) ||
            status[16] != 0xfc || status[51] != 0xfe)
        {
            throw Invalid("本次实机的 IRC / Flash 配置不在已核对布局内；未擦写。");
        }
        // USB 抓包没有现场 IRC 扫频；保留本芯片的当前校准，绝不复制测试板 trim。
        if (target.IrcHz is < 1000000 or > 48240000 || Math.Abs((double)target.IrcHz - requestedHz) / requestedHz > 0.005)
        {
            throw Invalid($"原生 USB 保留芯片当前 IRC（{target.IrcHz / 1000000m:0.######} MHz），与工程目标 {requestedHz / 1000000m:0.######} MHz 不符。请先用官方工具设置所需时钟，或选择支持调频的下载桥。");
        }
        var options = new byte[46];
        new byte[] { 4, 0, 0, 0x5a, 0xa5 }.CopyTo(options, 0);
        Convert.FromHexString("FFFFFF00FFFF00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00FF").CopyTo(options, 5);
        status.AsSpan(1, 16).CopyTo(options.AsSpan(29));
        options[42] |= 0x80; // 官方 32 位写入仅置运行模式位，其余硬件位保留本次实机值。
        options[45] = status[51];
        return options;
    }
    private static StudioXException Invalid(string message) => new("MX251_USB", message);
}
