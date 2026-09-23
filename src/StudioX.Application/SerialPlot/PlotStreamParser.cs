namespace StudioX.Application.SerialPlot;

using System.Globalization;
using System.Text;

/// <summary>跨 USB 包增量组帧；数据丢失时等到下一个组结束符再同步，禁止拼出伪数值。</summary>
public sealed class PlotStreamParser
{
    public const int MaximumChannels = 16;
    public const int MaximumRecordLength = 4096;
    private readonly string field;
    private readonly string terminator;
    private readonly Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly StringBuilder pending = new();
    private bool discarding;
    private bool firstCharacter = true;
    public int Channels { get; private set; }
    public PlotStreamParser(PlotFormat format) => (field, terminator) = format.Validate();

    public void LoseSynchronization()
    {
        decoder.Reset(); pending.Clear(); discarding = true;
    }

    public void Feed(ReadOnlySpan<byte> bytes, Action<double[]?, string?> record)
    {
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        int length;
        try { length = decoder.GetChars(bytes, chars, false); }
        catch (DecoderFallbackException)
        {
            LoseSynchronization(); record(null, "收到非 UTF-8 文本，已丢弃该数据块并重新同步。"); return;
        }
        for (var i = 0; i < length; i++)
        {
            var c = chars[i];
            if (firstCharacter) { firstCharacter = false; if (c == '\uFEFF') continue; }
            pending.Append(c);
            if (EndsWithTerminator())
            {
                if (!discarding) Parse(pending.ToString(0, pending.Length - terminator.Length), record);
                pending.Clear(); discarding = false;
            }
            else if (!discarding && pending.Length > MaximumRecordLength + terminator.Length)
            {
                discarding = true; record(null, $"单组超过 {MaximumRecordLength} 个字符，丢弃到下一个组结束符。");
            }
            if (discarding && pending.Length > terminator.Length) pending.Remove(0, pending.Length - terminator.Length);
        }
    }

    private bool EndsWithTerminator()
    {
        if (pending.Length < terminator.Length) return false;
        for (var i = 0; i < terminator.Length; i++)
            if (pending[pending.Length - terminator.Length + i] != terminator[i]) return false;
        return true;
    }
    private void Parse(string line, Action<double[]?, string?> record)
    {
        if (line.Length > MaximumRecordLength) { record(null, "数据组过长。"); return; }
        var parts = line.Split(field, StringSplitOptions.None);
        if (parts.Length > MaximumChannels || (Channels != 0 && parts.Length != Channels))
        { record(null, $"通道数不匹配：收到 {parts.Length}，期望 {(Channels == 0 ? "1–16" : Channels.ToString(CultureInfo.InvariantCulture))}。"); return; }
        var values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                !double.IsFinite(values[i]) || Math.Abs(values[i]) > 1e100)
            { record(null, $"第 {i + 1} 通道不是有效数值（支持小数、负数、科学计数，绝对值 ≤ 1e100）。"); return; }
        Channels = values.Length;
        record(values, null);
    }
}
