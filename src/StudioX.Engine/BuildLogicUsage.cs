namespace StudioX.Engine;

/// <summary>布局工具报告的逻辑单元数量；与固件和位流的字节数分别统计。</summary>
public sealed record BuildLogicUsage(ulong Used, ulong Capacity, string Source)
{
    public double Percent => Capacity == 0 ? 0 : Used * 100d / Capacity;
}
