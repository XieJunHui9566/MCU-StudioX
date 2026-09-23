namespace StudioX.SampleDecoder;

using System.Globalization;
using System.Text;
using StudioX.Extensions.Abstractions;

public sealed class SensorCsvDecoder : IFrameDecoder
{
    public DecodeResult Decode(ReadOnlyMemory<byte> payload)
    {
        var columns = Encoding.UTF8.GetString(payload.Span).Trim().Split(',');
        if (columns.Length != 2 || !double.TryParse(columns[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ||
            !double.IsFinite(temperature) || columns[1] is not ("0" or "1")) throw new FormatException("数据应为 temperature,0|1。");
        return new DecodeResult("传感器 CSV 帧", [new SignalValue("temperature", temperature, "°C"), new SignalValue("digital", int.Parse(columns[1], CultureInfo.InvariantCulture), "logic")]);
    }
}
