namespace StudioX.Devices;

public sealed record CaptureHeader(int FormatVersion, string ClockDomain, long TicksPerSecond, string ConnectionKey, bool Simulated);
