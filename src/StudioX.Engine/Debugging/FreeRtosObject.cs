namespace StudioX.Engine.Debugging;

public sealed record FreeRtosObject(
    ulong Address,
    string Name,
    string Kind,
    ulong? Count,
    ulong? Capacity,
    ulong? ItemSize,
    ulong? SendWaiters,
    ulong? ReceiveWaiters,
    ulong? MutexOwnerAddress,
    ulong? RecursionCount);
