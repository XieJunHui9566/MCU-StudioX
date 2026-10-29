using System.Buffers.Binary;

namespace StudioX.Ai8051Isp;

public sealed class BridgeProtocol(IHidReportTransport transport, Action<object> progress)
{
    private bool detected, prepared, programStarted, imageWritten;
    public static byte[] Encode(byte command, ReadOnlySpan<byte> data, uint offset = 0)
    {
        if (data.Length > 56)
        {
            throw new ArgumentOutOfRangeException(nameof(data));
        }
        var packet = new byte[65];
        packet[1] = 0x5a;
        packet[2] = command;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(3), offset);
        packet[7] = (byte)data.Length;
        data.CopyTo(packet.AsSpan(8));
        packet[64] = unchecked((byte)-packet.Take(64).Sum(x => x));
        return packet;
    }

    public static byte[] Decode(ReadOnlySpan<byte> report)
    {
        if (report.Length != 65 || report[0] != 0 || report[1] != 0xa5)
        {
            throw new InvalidDataException("Invalid HID reply header/size.");
        }
        int sum = 0;
        foreach (byte value in report)
        {
            sum += value;
        }
        if ((sum & 255) != 0)
        {
            throw new InvalidDataException("HID reply checksum mismatch.");
        }
        int length = BinaryPrimitives.ReadUInt16BigEndian(report[2..]);
        if (length > 60)
        {
            throw new InvalidDataException("HID reply payload exceeds 60 bytes.");
        }
        return report.Slice(4, length).ToArray();
    }

    private Task Send(byte command, byte[] data, CancellationToken token, uint offset = 0) => transport.Write(Encode(command, data, offset), token);
    private async Task<byte[]> Reply(int milliseconds, CancellationToken token) => Decode(await transport.Read(milliseconds, token));
    private async Task Control(string hex, int delayMs, CancellationToken token)
    {
        await Send(0x40, Convert.FromHexString(hex), token);
        if (delayMs > 0)
        {
            await Task.Delay(delayMs, token);
        }
    }
    private async Task<byte[]> Exchange(byte command, byte[] data, byte[] expectedPrefix, int timeout, CancellationToken token)
    {
        await Send(command, data, token);
        byte[] reply = await Reply(timeout, token);
        if (!reply.AsSpan().StartsWith(expectedPrefix))
        {
            throw new InvalidDataException($"Unexpected reply to {command:X2}: {Convert.ToHexString(reply)}; expected {Convert.ToHexString(expectedPrefix)}.");
        }
        return reply;
    }

    public async Task<TargetInfo> Detect(CancellationToken token)
    {
        if (programStarted && !imageWritten)
        {
            throw new InvalidOperationException("Partial programming session cannot be reused.");
        }
        detected = prepared = programStarted = imageWritten = false;
        byte[] bridge = await Exchange(0, [], [0x51, 1, 4, 0xff], 1500, token);
        if (bridge.Length != 19 || bridge[15] != 0x71)
        {
            throw new InvalidDataException("Unverified bridge firmware identity.");
        }
        await Control("0A01", 105, token);
        await Control("020201", 65, token);
        await Control("0400", 2, token);
        await Control("0328", 2, token);
        byte[] status = await Exchange(0x41, Convert.FromHexString("2800030500090000"), [0x50], 5000, token);
        if (status.Length != 54 || status[20] != 0x78 || status[21] != 0xb4 || status[17] != 0x74 || status[18] != 0x55 || status[22] != 0x11)
        {
            throw new InvalidDataException($"Target is outside verified AI8051U-34K64 / 7.4.1U profile: {Convert.ToHexString(status)}");
        }
        var info = new TargetInfo("AI8051U-34K64", "7.4.1U", "78B4", BinaryPrimitives.ReadUInt32BigEndian(status.AsSpan(1)), Convert.ToHexString(status));
        detected = true;
        progress(new
        {
            stage = "detected",
            target = info
        });
        return info;
    }

    public async Task<Calibration> Calibrate(DeviceConfiguration configuration, CancellationToken token)
    {
        if (!detected || prepared)
        {
            throw new InvalidOperationException("Calibration requires a freshly identified target.");
        }
        configuration.Validate();
        byte[] coarse = Convert.FromHexString("00080000FF000010FF100020FF200030FF30");
        byte[] counts = await Exchange(0x43, coarse, [0, 8], 3000, token);
        if (counts.Length != 18)
        {
            throw new InvalidDataException("Invalid coarse calibration length.");
        }
        double wanted = 0;
        int center = -1;
        byte range = 0, divider = 0;
        var ranges = new (int Low, int High)[4];
        for (int i = 0; i < 4; i++)
        {
            int low = BinaryPrimitives.ReadUInt16BigEndian(counts.AsSpan(2 + 4 * i));
            int high = BinaryPrimitives.ReadUInt16BigEndian(counts.AsSpan(4 + 4 * i));
            if (low == 0 || high <= low)
            {
                throw new InvalidDataException("Non-monotonic calibration range.");
            }
            ranges[i] = (low, high);
        }
        // 四个频段不连续（手册 8.4）；低频及频段间隙使用 CLKDIV，优先不分频。
        for (byte candidate = 1; candidate <= DeviceConfiguration.MaximumDivider && center < 0; candidate++)
        {
            long oscillatorHz = (long)configuration.IrcHz * candidate;
            if (oscillatorHz > DeviceConfiguration.MaximumIrcHz)
            {
                break;
            }
            wanted = oscillatorHz / 2400.0;
            for (int i = 0; i < ranges.Length; i++)
            {
                var (low, high) = ranges[i];
                if (wanted >= low && wanted <= high)
                {
                    center = (int)Math.Round((wanted - low) * 255 / (high - low));
                    range = coarse[3 + 4 * i];
                    divider = candidate;
                    break;
                }
            }
        }
        if (center < 0)
        {
            throw new InvalidDataException($"目标 {configuration.IrcHz} Hz 不在当前芯片可校准范围内；未擦写固件。");
        }
        var fine = new byte[26];
        fine[1] = 12;
        int start = Math.Clamp(center - 6, 0, 244);
        for (int i = 0; i < 12; i++)
        {
            fine[2 + 2 * i] = (byte)(start + i);
            fine[3 + 2 * i] = range;
        }
        byte[] measured = await Exchange(0x43, fine, [0, 12], 3000, token);
        if (measured.Length != 26)
        {
            throw new InvalidDataException("Invalid fine calibration length.");
        }
        int best = -1, bestCount = 0;
        double error = double.MaxValue;
        for (int i = 0; i < 12; i++)
        {
            int count = BinaryPrimitives.ReadUInt16BigEndian(measured.AsSpan(2 + 2 * i));
            if (Math.Abs(count - wanted) < error)
            {
                best = i;
                bestCount = count;
                error = Math.Abs(count - wanted);
            }
        }
        if (best < 0 || error / wanted > 0.005)
        {
            throw new InvalidDataException("IRC error exceeds 0.5%; no option write performed.");
        }
        var result = new Calibration(checked((uint)Math.Round(bestCount * 2400.0 / divider)), fine[2 + 2 * best], range, divider, configuration.IrcHz);
        DeviceConfiguration.BuildOptions(result.MeasuredHz, result.Trim, result.Range, result.Divider, result.RequestedHz);
        progress(new
        {
            stage = "calibrated",
            requestedHz = configuration.IrcHz,
            result
        });
        return result;
    }

    public async Task BeginProgramming(Calibration? calibration, CancellationToken token)
    {
        if (!detected || prepared)
        {
            throw new InvalidOperationException("Programming requires a freshly identified target.");
        }
        byte[] packet = Convert.FromHexString("010000FFCC000998");
        if (calibration != null)
        {
            packet[5] = calibration.Range;
            packet[6] = calibration.Trim;
        }
        await Exchange(0x43, packet, [1], 1500, token);
        await Control("026001", 100, token);
        await Exchange(0x43, Convert.FromHexString("0500005AA5"), [5], 1500, token);
        prepared = true;
    }

    public async Task WriteConfiguration(Calibration calibration, CancellationToken token)
    {
        // 此 BSL 的单独选项写入会清空选项却不完成；必须先收到所有代码块 ACK。
        if (!imageWritten)
        {
            throw new InvalidOperationException("Configuration requires a complete acknowledged firmware download in the same session.");
        }
        await Task.Delay(60, token);
        byte[] ack = await Exchange(0x43, DeviceConfiguration.BuildOptions(calibration.MeasuredHz, calibration.Trim, calibration.Range, calibration.Divider, calibration.RequestedHz), [4, 0x54], 3000, token);
        if (ack.Length != 2)
        {
            throw new InvalidDataException("Unexpected option acknowledgement length.");
        }
        progress(new
        {
            stage = "configuration-acknowledged",
            calibration
        });
    }

    public async Task Program(byte[] image, CancellationToken token)
    {
        if (!prepared || programStarted)
        {
            throw new InvalidOperationException("Programming session is not prepared or was already used.");
        }
        if (image.Length is < 1 or > 65536)
        {
            throw new InvalidDataException("Code image outside 64 KiB.");
        }
        programStarted = true;
        progress(new
        {
            stage = "erase-start",
            flashReadback = false
        });
        byte[] erased = await Exchange(0x43, Convert.FromHexString("0300005AA5"), [3], 5000, token);
        if (erased.Length != 8)
        {
            throw new InvalidDataException("Unexpected erase acknowledgement size.");
        }
        int padded = (image.Length + 63) / 64 * 64;
        for (int address = 0; address < padded; address += 64)
        {
            token.ThrowIfCancellationRequested();
            var block = new byte[69];
            Array.Fill(block, (byte)255);
            block[0] = (byte)(address == 0 ? 0x22 : 0x02);
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(1), (ushort)address);
            block[3] = 0x5a;
            block[4] = 0xa5;
            image.AsSpan(address, Math.Min(64, image.Length - address)).CopyTo(block.AsSpan(5));
            await Send(0x44, block[..56], token);
            await Send(0x44, block[56..], token, 56);
            byte[] ack = await Exchange(0x45, [0x45], [0x02, 0x54], 3000, token);
            if (ack.Length != 2)
            {
                throw new InvalidDataException("Unexpected write acknowledgement size.");
            }
            progress(new
            {
                stage = "program",
                acknowledgedBytes = address + 64,
                totalBytes = padded
            });
            await Task.Delay(20, token);
        }
        imageWritten = true;
    }

    public async Task Run(CancellationToken token)
    {
        if (programStarted && !imageWritten)
        {
            throw new InvalidOperationException("Refusing to run an incomplete image.");
        }
        await Control("0401", 110, token);
        await Control("0C01", 2, token);
        await Control("00", 520, token);
        await Control("0328", 110, token);
        await Control("0D01", 2, token);
        await Control("0A00", 2, token);
        await Send(0xff, [], token);
        progress(new
        {
            stage = "run-command-sent",
            acknowledged = false
        });
    }
}
