namespace StudioX.Engine;

/// <summary>封装引脚表中的固定电气连接，与 VE 中可映射的恒高、恒低信号分开。</summary>
public sealed record Ag32FixedPin(string Name, Ag32FixedPinKind Kind, string Description);
