namespace StudioX.Application.OpenOcdPlot;

public sealed record OpenOcdPlotReading(DateTimeOffset HostTime, bool TargetRunning, double[] Values);
