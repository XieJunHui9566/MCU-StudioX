namespace StudioX.MakerPlugins;

public sealed record PidRun(PidSettings Settings, PidSample[] Samples, PidMetrics Metrics);
