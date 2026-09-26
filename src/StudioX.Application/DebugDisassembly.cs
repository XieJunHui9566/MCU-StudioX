namespace StudioX.Application;

using StudioX.Engine.Debugging;

/// <summary>暂停时的一段只读反汇编；PC 始终来自最内层栈帧，独立于选中的调用者。</summary>
public sealed record DebugDisassembly(uint StartAddress, uint EndAddress, uint FocusAddress, uint? ProgramCounter, DebugInstruction[] Instructions);
