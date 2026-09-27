namespace StudioX.Engine.Debugging;

public sealed record FreeRtosHeap(
    string Kind,
    ulong? TotalBytes,
    ulong? FreeBytes,
    ulong? MinimumEverFreeBytes,
    ulong? AllocationCount,
    ulong? FreeCount,
    ulong? LargestFreeBlockBytes,
    ulong? FreeBlockCount);
