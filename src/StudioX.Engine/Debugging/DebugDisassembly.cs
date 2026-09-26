namespace StudioX.Engine.Debugging;

using System.Globalization;

/// <summary>由 GDB 返回的一条指令；不按目标架构猜测指令宽度或源码对应关系。</summary>
public sealed record DebugInstruction(uint Address, string Opcodes, string Instruction, string Function, string Offset)
{
    public string AddressText => "0x" + Address.ToString("x8", CultureInfo.InvariantCulture);
    public string Symbol => Function.Length == 0 ? "" : Offset.Length == 0 || Offset == "0" ? Function : Function + "+" + Offset;
}
