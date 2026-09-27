namespace StudioX.Application.Serial;

public sealed record ProtocolSnapshot(long Version, ProtocolFrame[] Frames, long Discarded, long Dropped,
    string Message, bool Failed);
