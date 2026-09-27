namespace StudioX.Application.Serial;

public sealed record SerialRecord(DateTimeOffset Time, bool Transmit, byte[] Data, bool Boundary = false, bool Offline = false);
