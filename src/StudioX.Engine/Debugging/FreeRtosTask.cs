namespace StudioX.Engine.Debugging;

public sealed record FreeRtosTask(
    ulong Address,
    string Name,
    string State,
    ulong? Priority,
    ulong? BasePriority,
    ulong? StackAddress,
    ulong? StackPointer,
    ulong? StackHighWaterBytes,
    ulong? StackSizeBytes,
    ulong? RuntimeCounter);
