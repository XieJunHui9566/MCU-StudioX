namespace StudioX.Application.Serial;

public sealed record SerialRawSnapshot(long AvailableFromByteOffset, long NextReceivedByteOffset,
    long TotalReceivedBytes, long MissingHistoryBytes, long DroppedFrames, bool Truncated,
    IReadOnlyList<SerialRawChunk> Chunks);
