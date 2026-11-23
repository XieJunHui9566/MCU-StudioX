namespace StudioX.Engine;

/// <summary>一个简单 MCU 功能到封装脚的映射；空方向沿用厂商功能定义。</summary>
public sealed record Ag32PinAssignment(string Function, int PinNumber, string? Direction = null, string? Name = null,
    string Pull = "NONE", string OutputType = "PUSH_PULL")
{
    public bool InitialHigh
    {
        get; init;
    }
    public bool SlewControl
    {
        get; init;
    }
    public string Interrupt { get; init; } = "NONE";
    public int InterruptPriority { get; init; } = 1;
}
