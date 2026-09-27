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
