namespace StudioX.Application.OpenOcdPlot;

public sealed record OpenOcdPlotChannel(Guid SessionId, string Expression, uint Address, PlotScalar Type, bool LittleEndian)
{
    public string AddressText => $"0x{Address:X8}";
}
