namespace StudioX.Engine.StcUsb;

using System.Buffers.Binary;
using StudioX.Ai8051Isp;
using StudioX.Foundation;

/// <summary>STC8H8K64U 工厂 USB 协议；Windows 分包及应答依据 2026-10-31 官方工具通信记录。</summary>
public sealed class StcUsbProtocol(IHidReportTransport transport)
{
    public const int CodeBytes = 65024;
    public const ushort ModelCode = 0xf784;
    private byte[]? status;

    public async Task IdentifyAsync(CancellationToken token)
    {
        var reply = await ExchangeAsync([0x00], 3000, token);
        if (reply.Length != 54 || reply[0] != 0 || BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(20)) != ModelCode)
        {
            throw Invalid("USB 实机不是 STC8H8K64U，或身份应答长度不受支持；未擦除或写入。");
        }
        // 未测 Boot ROM 版本不能仅因共用 VID/PID 而获得擦写资格。
        if (reply[17] != 0x74 || reply[18] != (byte)'U' || reply[22] != 0x0c)
        {
            throw Invalid("当前 USB 适配已核对 ISP 7.4.12U；检测到的 Boot ROM 版本尚待核对，未擦除或写入。");
        }
        status = reply;
    }

    public async Task ProgramAsync(byte[] binary, StcIspSettings settings, IProgress<string>? output, CancellationToken token)
    {
        if (status is null || binary.Length is <= 0 or > CodeBytes)
        {
            throw Invalid("必须先核对准确型号，且固件须在 65024 字节用户代码区内。");
        }
        var padded = checked((binary.Length + 127) / 128 * 128);
        if (padded > CodeBytes)
        {
            throw Invalid("补齐后的 USB 写入超出用户代码区。");
        }
        // 在擦除前构造并检查选项；禁止将另一颗芯片的校准字节固化为常量。
        var options = CreateOptions(settings);
        await ExpectAsync([0x01, 0, 0, 0, 0, 0, 0, 0x80], [0x01], 3000, token);
        await ExpectAsync([0x05, 0, 0, 0x5a, 0xa5], [0x05], 3000, token);
        output?.Report("Erasing flash: USB\n");
        var erased = await ExchangeAsync([0x03, 0, 0, 0x5a, 0xa5], 10000, token);
        if (erased.Length != 8 || erased[0] != 3 || BinaryPrimitives.ReadUInt16BigEndian(erased.AsSpan(1)) != ModelCode)
        {
            throw Invalid("USB 擦除应答无效；未继续写入或运行。");
        }
        var addresses = Enumerable.Range(0, padded / 128).Select(index => index * 128)
            .Where(address => address == 0 || binary.AsSpan(address, Math.Min(128, binary.Length - address)).ContainsAnyExcept((byte)0xff)).ToArray();
        var written = 0;
        if (addresses.Length < padded / 128)
        {
            output?.Report($"USB：跳过 {padded / 128 - addresses.Length} 个已擦除的空白页。\n");
        }
        foreach (var address in addresses)
        {
            var packet = new byte[133];
            packet.AsSpan().Fill(0xff);
            packet[0] = written == 0 ? (byte)0x32 : (byte)0x12;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), checked((ushort)address));
            packet[3] = 0x5a;
            packet[4] = 0xa5;
            binary.AsSpan(address, Math.Min(128, binary.Length - address)).CopyTo(packet.AsSpan(5));
            await ExpectAsync(packet, [0x02, 0x54], 5000, token);
            written += 128;
            output?.Report($"STUDIOX_ISP_PROGRESS {written} {addresses.Length * 128}\n");
        }
        output?.Report(settings.ClockMode == StcClockMode.Preserve
            ? "Setting options: USB · 保留本次实机时钟\n"
            : "Setting options: USB · 内部 RC 24 MHz，使用本芯片校准值\n");
        if (settings.ModifyHardware && settings.Hardware?.Values() is { Count: > 0 } changed)
        {
            output?.Report($"USB 硬件选项：覆盖 {string.Join("、", changed.Keys)}，其它位保留本次实机值。\n");
        }
        await ExpectAsync(options, [0x04, 0x54], 5000, token);
        output?.Report("Finishing write: USB\n");
        await SendAsync([0xff], token);
        output?.Report("STUDIOX_PROGRAM_COMPLETE\n");
    }

    private byte[] CreateOptions(StcIspSettings settings)
    {
        if (settings.DownloadMode != StcDownloadMode.NativeUsb ||
            settings.ClockMode != StcClockMode.Preserve &&
            !(settings.ClockMode == StcClockMode.InternalRc && settings.ClockFrequencyHz == 24000000))
        {
            throw Invalid("USB 时钟设置尚未核对；未擦除或写入。");
        }
        var options = new byte[45];
        new byte[] { 4, 0, 0, 0x5a, 0xa5 }.CopyTo(options, 0);
        Convert.FromHexString("FFFFFF00FFFF00FFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00FF").CopyTo(options, 5);
        var current = status ?? throw Invalid("必须先读取实机身份。");
        // 当前构建契约固定 65024 B 代码 / 512 B EEPROM。分区不符时不能按最大代码区擦写。
        if (current[16] != 0xfe)
        {
            throw Invalid("实机 Flash / EEPROM 分区与当前工程的 65024 B 代码区不一致；未擦除或写入。USB 分区修改尚未支持。");
        }
        current.AsSpan(1, 16).CopyTo(options.AsSpan(29));
        if (settings.ClockMode == StcClockMode.InternalRc)
        {
            // 7.4.12U 的 24 MHz 出厂校准槽由官方工具改频记录核对；不推定其它 ROM 或频率。
            if (current[26] is 0 or 0xff || current[28] != 0x20)
            {
                throw Invalid("本芯片的 24 MHz 校准槽无效；未擦除或写入。");
            }
            BinaryPrimitives.WriteInt32BigEndian(options.AsSpan(29), 24000000);
            options[33] = current[26];
            options[34] = current[28];
            options[35] = 1;
        }
        StcUsbHardwareOptions.Apply(options, settings.Hardware, settings.ModifyHardware);
        return options;
    }

    private readonly StcUsbFrames frames = new(transport);
    private Task ExpectAsync(byte[] payload, byte[] expected, int timeoutMs, CancellationToken token)
        => frames.ExpectAsync(payload, expected, timeoutMs, token);
    private Task<byte[]> ExchangeAsync(byte[] payload, int timeoutMs, CancellationToken token)
        => frames.ExchangeAsync(payload, timeoutMs, token);
    private Task SendAsync(byte[] payload, CancellationToken token) => frames.SendAsync(payload, token);

    private static StudioXException Invalid(string message) => new("STC_USB_PROTOCOL", message);
}
