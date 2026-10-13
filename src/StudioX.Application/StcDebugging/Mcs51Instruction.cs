namespace StudioX.Application.StcDebugging;

/// <summary>8051 指令及控制流；A5 是 STC Mon51 断点陷阱，不当作普通指令执行。</summary>
public sealed record Mcs51Instruction(ushort Address, byte Opcode, int Length, string Text, byte[] Bytes, ushort? Target = null)
{
    public bool IsCall => Opcode == 0x12 || (Opcode & 0x1f) == 0x11;
    public bool IsReturn => Opcode is 0x22 or 0x32;
    public bool IsBranch => IsCall || IsReturn || (Opcode & 0x1f) == 1 || Opcode is 0x02 or 0x10 or 0x20 or 0x30 or 0x40 or 0x50 or 0x60 or 0x70 or 0x73 or 0x80 or 0xd5 || Opcode is >= 0xb4 and <= 0xbf || Opcode >= 0xd8 && Opcode <= 0xdf;
}
