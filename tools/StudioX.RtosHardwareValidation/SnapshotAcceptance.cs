namespace StudioX.RtosHardwareValidation;

using System.Text.RegularExpressions;
using StudioX.Engine.Debugging;

internal sealed record AcceptanceReport(bool Accepted, string[] Errors, string[] PartialFields);

/// <summary>验收一致性检查与目标访问分离；矛盾数据保留，但不能作为成功硬件证据。</summary>
internal static class SnapshotAcceptance
{
    public static AcceptanceReport Validate(FreeRtosSnapshot snapshot, ulong ramBytes)
    {
        var errors = new List<string>();
        var partial = new List<string>();
        if (!snapshot.IsAvailable) errors.Add("没有可读取的 FreeRTOS 内核，不能确认 RTOS 实板验收。");
        if (snapshot.SchedulerRunning is null || snapshot.SchedulerSuspended is null)
            errors.Add("调度器启动或挂起计数不可读，不能核对内核状态。");
        if (snapshot.SchedulerSuspended > uint.MaxValue)
            errors.Add("调度器挂起计数超过 32 位目标字段范围。");
        if (snapshot.ReportedTaskCount is null)
            errors.Add("内核任务计数不可读，不能确认任务列表完整。");
        var tasks = snapshot.Tasks ?? [];
        if (snapshot.ReportedTaskCount != (ulong)tasks.Count)
            errors.Add("内核任务计数与可读任务列表数量不一致。");
        // 每个 TCB 至少占用一个目标指针；由实际器件 RAM 派生，不写死任务数量。
        if (ramBytes == 0 || snapshot.ReportedTaskCount > ramBytes / sizeof(uint))
            errors.Add("内核任务数超出器件 RAM 可容纳的最小 TCB 数量。");
        if (tasks.Select(task => task.Address).Distinct().Count() != tasks.Count)
            errors.Add("可读任务列表包含重复 TCB 地址。");
        if (tasks.Any(task => !ValidPointer(task.Address)))
            errors.Add("任务列表包含空、未对齐或非 32 位 TCB 地址。");
        if (snapshot.SchedulerRunning == true)
        {
            if (snapshot.CurrentTaskAddress is not { } current || !ValidPointer(current) ||
                !tasks.Any(task => task.Address == current))
                errors.Add("调度器已启动，但当前 TCB 不是列表中的有效对齐地址。");
            if (tasks.Count(task => task.State == "Running") != 1 ||
                !tasks.Any(task => task.State == "Running" && task.Address == snapshot.CurrentTaskAddress))
                errors.Add("任务 Running 标记与调度器当前 TCB 不一致。");
        }
        else if (snapshot.SchedulerRunning == false)
        {
            if (tasks.Any(task => task.State == "Running")) errors.Add("调度器未启动，却存在 Running 任务。");
            if (snapshot.CurrentTaskAddress is > 0 && (!ValidPointer(snapshot.CurrentTaskAddress.Value) ||
                !tasks.Any(task => task.Address == snapshot.CurrentTaskAddress)))
                errors.Add("调度器未启动时的非空当前 TCB 也必须是有效列表项。");
            partial.Add("调度器尚未启动，本次没有确认任务运行。");
        }
        if (snapshot.TickCount is null) partial.Add("Tick 不可读。");
        if (tasks.Any(task => task.StackSizeBytes is null || task.StackHighWaterBytes is null)) partial.Add("部分任务栈容量或水位不可读。");
        if (tasks.Any(task => task.RuntimeCounter is null)) partial.Add("部分任务运行时间计数不可读。");

        if (snapshot.Heap is { } heap)
        {
            if (heap.TotalBytes is null) partial.Add("堆总容量不可读，按器件 RAM 总预算检查现有统计。");
            if (heap.TotalBytes is 0 || heap.TotalBytes > ramBytes) errors.Add("堆总容量为空容量或超过本次器件声明的 RAM 预算。");
            var budget = heap.TotalBytes ?? ramBytes;
            if (heap.FreeBytes is null) partial.Add("堆当前空闲字节不可读。");
            if (heap.MinimumEverFreeBytes is null) partial.Add("堆历史最低空闲不可读。");
            if (heap.LargestFreeBlockBytes is null || heap.FreeBlockCount is null) partial.Add("堆空闲链表统计不完整。");
            if (heap.FreeBytes > budget || heap.MinimumEverFreeBytes > budget || heap.LargestFreeBlockBytes > budget)
                errors.Add("堆当前/历史最低/最大块统计超过可确认容量。");
            if (heap.MinimumEverFreeBytes > heap.FreeBytes) errors.Add("堆历史最低空闲大于当前空闲量。");
            if (heap.LargestFreeBlockBytes > heap.FreeBytes) errors.Add("最大空闲块大于全部空闲字节。");
            if (heap.FreeBlockCount > budget / sizeof(uint)) errors.Add("空闲块数量超过容量可容纳的最小块数。");
            if (heap.FreeBlockCount == 0 && (heap.FreeBytes > 0 || heap.LargestFreeBlockBytes > 0))
                errors.Add("没有空闲块却有非零空闲字节或最大空闲块。");
            if (heap.FreeBlockCount > 0 && (heap.FreeBytes == 0 || heap.LargestFreeBlockBytes == 0))
                errors.Add("存在空闲块却报告零空闲字节或零最大空闲块。");
            if (heap.FreeBlockCount == 1 && heap.FreeBytes is not null && heap.LargestFreeBlockBytes is not null &&
                heap.FreeBytes != heap.LargestFreeBlockBytes)
                errors.Add("单一空闲块与最大块/空闲总量不一致。");
        }
        else partial.Add("当前分配器没有可读的完整堆快照。");
        return new(errors.Count == 0, errors.ToArray(), partial.ToArray());
    }

    private static bool ValidPointer(ulong address) => address is > 0 and <= uint.MaxValue && address % sizeof(uint) == 0;
}

/// <summary>只接受工具实际输出的校验结果；MI 的 done 本身不代表映像匹配。</summary>
internal sealed class ImageVerificationEvidence
{
    public bool HasPositiveVerified { get; private set; }
    public bool HasMismatch { get; private set; }
    public bool HasChecksumWarning { get; private set; }
    public bool HasTransportFailure { get; private set; }
    public bool Accepted => HasPositiveVerified && !HasMismatch && !HasTransportFailure;

    public void AddLogLine(string line)
    {
        string? payload = null;
        var openOcd = line.IndexOf(" OpenOCD < ", StringComparison.Ordinal);
        if (openOcd >= 0) payload = line[(openOcd + " OpenOCD < ".Length)..];
        var gdb = line.IndexOf(" GDB < ", StringComparison.Ordinal);
        if (gdb >= 0)
        {
            var text = line[(gdb + " GDB < ".Length)..];
            if (text.Length > 0 && text[0] is '@' or '~' or '&') payload = MiRecord.Parse(text).Data.Text;
        }
        if (payload is null) return;
        // CRC 不同可能进入逐字节回退；完整 binary compare 成功且没有 diff 时不能仅因 CRC 警告拒绝。
        if (Regex.IsMatch(payload, @"checksum\s+mismatch", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) HasChecksumWarning = true;
        if (Regex.IsMatch(payload, @"\bdiff\s+\d+\s+address\b|verify(?:_image)?\s+failed", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            HasMismatch = true;
        if (new[] { "LIBUSB_ERROR", "error reading USB data", "error writing USB data", "CMD_INFO failed", "CMD_CONNECT failed",
                    "CMD_DISCONNECT failed", "error reading adapter response" }.Any(marker => payload.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            HasTransportFailure = true;
        foreach (Match match in Regex.Matches(payload, @"\bverified\s+(\d+)\s+bytes\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            if (ulong.TryParse(match.Groups[1].Value, out var bytes) && bytes > 0) HasPositiveVerified = true;
    }
}
