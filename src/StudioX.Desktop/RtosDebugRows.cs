namespace StudioX.Desktop;

using System.Globalization;
using StudioX.Engine.Debugging;

internal static class RtosDisplay
{
    internal static string Number(ulong? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";
    internal static string Address(ulong? value) => value is { } address ? $"0x{address:x8}" : "—";
    internal static string State(string state) => state switch
    {
        "Running" => "当前任务", "Ready" => "就绪", "Blocked" => "阻塞", "Suspended" => "挂起",
        "PendingReady" => "待就绪", "Deleted" => "等待回收", "SuspendedOrBlocked" => "挂起/阻塞", _ => "未知"
    };
    internal static string Kind(string kind) => kind switch
    {
        "Queue" => "队列", "BinarySemaphore" => "二值信号量", "CountingSemaphore" => "计数信号量",
        "Mutex" => "互斥锁", "RecursiveMutex" => "递归互斥锁", "QueueSet" => "队列集",
        "QueueOrQueueSet" => "队列/队列集", "Semaphore" => "信号量", _ => "类型未记录"
    };
}

internal sealed record RtosTaskRow(FreeRtosTask Data)
{
    public string Name => Data.Name;
    public string State => RtosDisplay.State(Data.State);
    public string Priority => RtosDisplay.Number(Data.Priority);
    public string BasePriority => RtosDisplay.Number(Data.BasePriority);
    public string StackMinimum => RtosDisplay.Number(Data.StackHighWaterBytes);
    public string StackSize => RtosDisplay.Number(Data.StackSizeBytes);
    public bool IsCurrent => Data.State == "Running";
    public string Details => $"TCB {RtosDisplay.Address(Data.Address)}   栈起点 {RtosDisplay.Address(Data.StackAddress)}   " +
        $"保存的栈指针 {RtosDisplay.Address(Data.StackPointer)}   运行计数 {RtosDisplay.Number(Data.RuntimeCounter)}";
}

internal sealed record RtosObjectRow(FreeRtosObject Data, IReadOnlyList<FreeRtosTask> Tasks)
{
    public string Name => Data.Name;
    public string Kind => RtosDisplay.Kind(Data.Kind);
    public string Count => RtosDisplay.Number(Data.Count);
    public string Capacity => RtosDisplay.Number(Data.Capacity);
    public string ItemSize => RtosDisplay.Number(Data.ItemSize);
    public string SendWaiters => RtosDisplay.Number(Data.SendWaiters);
    public string ReceiveWaiters => RtosDisplay.Number(Data.ReceiveWaiters);
    public string Owner => Data.MutexOwnerAddress is null ? "—" : Data.MutexOwnerAddress == 0 ? "未持有" :
        Tasks.FirstOrDefault(task => task.Address == Data.MutexOwnerAddress)?.Name ?? RtosDisplay.Address(Data.MutexOwnerAddress);
    public string Details => $"对象 {RtosDisplay.Address(Data.Address)}   持有者 {Owner}   递归计数 {RtosDisplay.Number(Data.RecursionCount)}";
}

internal sealed record RtosHeapRow(string Name, string Value);
