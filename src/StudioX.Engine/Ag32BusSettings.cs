namespace StudioX.Engine;

public sealed record Ag32BusSettings(string Module, int? ClockDivider = null, bool BigEndian = false,
    int? BitRate = null, int? SamplePointPermille = null)
{
    public int? RxDmaChannel
    {
        get; init;
    }
    public int DmaPriority { get; init; } = 1;
    public int? TxDmaChannel
    {
        get; init;
    }
    public string FilterMode { get; init; } = "ALL";
    public int FilterId
    {
        get; init;
    }
    public int CompareMask
    {
        get; init;
    }
    public int InterruptMask
    {
        get; init;
    }
    public int InterruptPriority { get; init; } = 1;
}
