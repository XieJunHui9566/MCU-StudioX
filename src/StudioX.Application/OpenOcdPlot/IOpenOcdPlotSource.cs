namespace StudioX.Application.OpenOcdPlot;

public interface IOpenOcdPlotSource
{
    Guid PlotSessionId { get; }
    bool CanPlot { get; }
    string PlotTarget { get; }
    Task<OpenOcdPlotChannel> ResolvePlotChannelAsync(string expression, PlotScalar type, CancellationToken token = default);
    Task<OpenOcdPlotReading> ReadPlotAsync(IReadOnlyList<OpenOcdPlotChannel> channels, CancellationToken token = default);
}

public sealed record OpenOcdPlotReading(DateTimeOffset HostTime, bool TargetRunning, double[] Values);
