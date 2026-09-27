namespace StudioX.Engine;

public sealed record DownloadOptions(string ProbeId, int SpeedKhz, string? Serial = null);
