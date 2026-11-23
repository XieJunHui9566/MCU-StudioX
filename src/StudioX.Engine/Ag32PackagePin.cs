namespace StudioX.Engine;

/// <summary>可映射性取自厂商工具，固定电气连接取自对应封装手册。</summary>
public sealed record Ag32PackagePin(int Number, bool CanAssign)
{
    public string? AlternateFunction
    {
        get; init;
    }
    public Ag32FixedPin? FixedPin
    {
        get; init;
    }
}
