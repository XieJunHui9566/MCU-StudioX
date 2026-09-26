namespace StudioX.Desktop;

using StudioX.Engine.Debugging;

public sealed record DebugDisassemblyRow(DebugInstruction Data, bool IsCurrent)
{
    public string Indicator => IsCurrent ? "▶" : "";
    public string AddressText => Data.AddressText;
    public string Opcodes => Data.Opcodes;
    public string Instruction => Data.Instruction;
    public string Symbol => Data.Symbol;
}
