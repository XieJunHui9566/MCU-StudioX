namespace StudioX.Application.SerialPlot;

public sealed record PlotSnapshot(long Version, bool Connected, bool Simulated, string Message, string? LastError,
    long ReceivedBytes, long ValidRecords, long InvalidRecords, long DroppedFrames, long EvictedRecords,
    int Channels, PlotFormat Format, PlotSample[] Samples);
