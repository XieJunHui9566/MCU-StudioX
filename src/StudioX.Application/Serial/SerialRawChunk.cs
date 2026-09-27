namespace StudioX.Application.Serial;

public sealed record SerialRawChunk(DateTimeOffset ReceivedAtUtc, long OffsetBytes, string Base64);
