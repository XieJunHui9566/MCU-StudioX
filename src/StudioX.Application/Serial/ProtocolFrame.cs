namespace StudioX.Application.Serial;

public enum SerialProtocol { None, ModbusRtu, JavaScript }
public enum ModbusRole { PcMaster, PcSlave, Monitor }
public sealed record ProtocolOptions(SerialProtocol Protocol = SerialProtocol.None,
    ModbusRole Role = ModbusRole.PcMaster, int IdleMilliseconds = 100, string? ScriptPath = null);
public sealed record ProtocolField(string Name, string Value);
public sealed record ProtocolFrame(DateTimeOffset Time, string Direction, string Protocol, string Address,
    string Function, string Status, string Summary, string Hex, IReadOnlyList<ProtocolField> Fields)
{
    public long Sequence { get; init; }
    public string Source { get; init; } = "串口";
    public string LocalTime => Time.ToLocalTime().ToString("HH:mm:ss.fff");
    public string Detail => "来源：" + Source + Environment.NewLine + string.Join(Environment.NewLine, Fields.Select(f => f.Name + "：" + f.Value)) +
        Environment.NewLine + Environment.NewLine + "原始字节：" + Hex;
}
public sealed record ProtocolSnapshot(long Version, ProtocolFrame[] Frames, long Discarded, long Dropped,
    string Message, bool Failed);
