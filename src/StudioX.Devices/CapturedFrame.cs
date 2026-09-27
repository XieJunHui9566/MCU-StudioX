namespace StudioX.Devices;

public sealed record CapturedFrame(long Sequence, long Timestamp, string Base64);
