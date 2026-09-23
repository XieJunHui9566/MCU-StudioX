namespace StudioX.Application.SerialPlot;

using System.Text;

public sealed record PlotFormat(string FieldSeparator = ",", string RecordSeparator = "\\n", double SampleIntervalMs = 0)
{
    public (string Field, string Record) Validate()
    {
        var field = DecodeSeparator(FieldSeparator);
        var record = DecodeSeparator(RecordSeparator);
        if (field.Length is < 1 or > 16 || record.Length is < 1 or > 16)
            throw new ArgumentException("分隔符不能为空，解码后最多 16 个字符。");
        if (field.Contains(record, StringComparison.Ordinal) || record.Contains(field, StringComparison.Ordinal))
            throw new ArgumentException("通道分隔符与组结束符不能相同或互相包含。");
        if (!double.IsFinite(SampleIntervalMs) || SampleIntervalMs < 0 || SampleIntervalMs > 3600000)
            throw new ArgumentException("采样周期须为 0–3600000 ms；0 使用电脑接收时间。");
        return (field, record);
    }

    public static string DecodeSeparator(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { result.Append(value[i]); continue; }
            if (++i == value.Length) throw new ArgumentException("分隔符末尾不能是单独的反斜杠。");
            result.Append(value[i] switch
            {
                'r' => '\r', 'n' => '\n', 't' => '\t', '\\' => '\\',
                _ => throw new ArgumentException("分隔符支持 \\r、\\n、\\t、\\\\ 转义，其他字符直接输入。")
            });
        }
        return result.ToString();
    }
}
