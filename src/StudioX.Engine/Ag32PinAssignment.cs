namespace StudioX.Engine;

/// <summary>一个简单 MCU 功能到封装脚的映射；空方向沿用厂商功能定义。</summary>
public sealed record Ag32PinAssignment(string Function, int PinNumber, string? Direction = null);
