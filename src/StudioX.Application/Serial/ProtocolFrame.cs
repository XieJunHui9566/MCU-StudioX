namespace StudioX.Application.Serial;

public sealed record ProtocolFrame(DateTimeOffset Time, string Direction, string Protocol, string Address,
    string Function, string Status, string Summary, string Hex, IReadOnlyList<ProtocolField> Fields)
{
    public long Sequence
    {
        get; init;
    }
    public string Source { get; init; } = "串口";
    public string LocalTime => Time.ToLocalTime().ToString("HH:mm:ss.fff");
    public string Detail => "来源：" + Source + Environment.NewLine + string.Join(Environment.NewLine, Fields.Select(f => f.Name + "：" + f.Value)) +
        Environment.NewLine + Environment.NewLine + "原始字节：" + Hex;
}
