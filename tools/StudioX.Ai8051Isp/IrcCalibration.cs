using System.Buffers.Binary;
namespace StudioX.Ai8051Isp;

/// <summary>桥接与串口共用 IRC 范围、分频和最小误差选择；传输只提供计数。</summary>
public static class IrcCalibration
{
    public static async Task<Calibration> MeasureAsync(DeviceConfiguration configuration,
        Func<byte[], byte[], CancellationToken, Task<byte[]>> exchange, Action<object> progress, CancellationToken token)
    {
        configuration.Validate();
        byte[] coarse = Convert.FromHexString("00080000FF000010FF100020FF200030FF30");
        byte[] counts = await exchange(coarse, [0, 8], token);
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
        byte[] measured = await exchange(fine, [0, 12], token);
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
}
