namespace StudioX.Engine.Debugging;

/// <summary>暂停目标时读取的内核数据；空值表示目标未提供该字段，不替换为零。</summary>
public sealed record FreeRtosSnapshot(
    bool IsAvailable,
    string? KernelVersion,
    bool? SchedulerRunning,
    ulong? SchedulerSuspended,
    ulong? TickCount,
    ulong? CurrentTaskAddress,
    ulong? ReportedTaskCount,
    IReadOnlyList<FreeRtosTask> Tasks,
    FreeRtosHeap? Heap,
    IReadOnlyList<FreeRtosObject> Objects,
    IReadOnlyList<string> Diagnostics);

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

public sealed record FreeRtosHeap(
    string Kind,
    ulong? TotalBytes,
    ulong? FreeBytes,
    ulong? MinimumEverFreeBytes,
    ulong? AllocationCount,
    ulong? FreeCount,
    ulong? LargestFreeBlockBytes,
    ulong? FreeBlockCount);

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
