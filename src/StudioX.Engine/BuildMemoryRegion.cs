namespace StudioX.Engine;

public sealed record BuildMemoryRegion(string Name, ulong Origin, ulong Capacity, ulong Used, bool IsLogical = false)
{
    public double Percent => Capacity == 0 ? 0 : (double)Used / Capacity * 100;
}
