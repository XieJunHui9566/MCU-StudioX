namespace StudioX.Extensions.Abstractions;

/// <summary>程序员计算器的有界显示数据；计算仍在插件进程内进行，宿主只处理输入和呈现。</summary>
public sealed record PluginProgrammerCalculatorState(
    int FormatVersion, int Width, int Radix, bool SignedMode, string Expression,
    string Hex, string DecimalText, string Octal, string Binary,
    string Unsigned, string Signed, string LittleEndian, string BigEndian, string CopyText,
    bool FieldMode = false, int Start = 0, int Count = 1, string Operation = "inspect", string Operand = "0",
    string? Error = null, string FieldValue = "0", string FieldMask = "0")
{
    public int RequestId
    {
        get; init;
    }
    public bool Preview
    {
        get; init;
    }
    public bool Pending
    {
        get; init;
    }
    public bool Realtime
    {
        get; init;
    }
}
