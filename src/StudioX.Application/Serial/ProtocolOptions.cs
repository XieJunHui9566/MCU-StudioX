namespace StudioX.Application.Serial;

public sealed record ProtocolOptions(SerialProtocol Protocol = SerialProtocol.None,
    ModbusRole Role = ModbusRole.PcMaster, int IdleMilliseconds = 100, string? ScriptPath = null);
