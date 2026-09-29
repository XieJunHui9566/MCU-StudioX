namespace StudioX.Application.OpenOcdPlot;

using StudioX.Application.SerialPlot;

public sealed record OpenOcdPlotSnapshot(bool Capturing, string Status, string? Error, string Target,
    OpenOcdPlotChannel[] Channels, OpenOcdPlotRecord[] Records, long MissedIntervals, long EvictedRecords, long InvalidRecords, int IntervalMs)
{
    public PlotSample[] Samples => Records.Select(row => new PlotSample(row.Seconds, row.Values, row.Segment)).ToArray();
}

public sealed record OpenOcdPlotRecord(double Seconds, DateTimeOffset HostTime, bool TargetRunning, double[] Values, long Segment);
