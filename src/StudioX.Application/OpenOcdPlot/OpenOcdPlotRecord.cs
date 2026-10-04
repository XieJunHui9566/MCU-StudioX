namespace StudioX.Application.OpenOcdPlot;

public sealed record OpenOcdPlotRecord(double Seconds, DateTimeOffset HostTime, bool TargetRunning, double[] Values, long Segment);
