namespace StudioX.Engine.Debugging;

using System.Globalization;
using StudioX.Foundation;

public sealed partial class FreeRtosInspector
{
    private sealed partial class Reader
    {
        private string listContainerField = "";

        private async Task<FreeRtosTask[]> ReadTasksAsync(ulong? current, bool schedulerRunning, ulong? reportedCount)
        {
            listContainerField = await HasMemberAsync("ListItem_t", "pxContainer") ? "pxContainer" :
                await HasMemberAsync("ListItem_t", "pvContainer") ? "pvContainer" : "";
            var states = new Dictionary<ulong, string>();
            var priorities = await NumberAsync("sizeof(" + TaskGlobal("pxReadyTasksLists") + ")/sizeof(" + TaskGlobal("pxReadyTasksLists[0]") + ")", "就绪优先级数量");
            if (priorities is > 0 and <= MaximumArrayEntries)
            {
                for (ulong priority = 0; priority < priorities; priority++)
                    await AddListAsync(TaskGlobal("pxReadyTasksLists[" + priority.ToString(CultureInfo.InvariantCulture) + "]"), "Ready", false);
            }
            else if (priorities is not null) diagnostics.Add("就绪列表数量异常，未遍历：" + priorities);
            await AddListAsync(TaskGlobal("xDelayedTaskList1"), "Blocked", false);
            await AddListAsync(TaskGlobal("xDelayedTaskList2"), "Blocked", false);
            await AddListAsync(TaskGlobal("xSuspendedTaskList"), "Suspended", true);
            await AddListAsync(TaskGlobal("xTasksWaitingTermination"), "Deleted", true);
            // PendingReady 使用事件列表项；同一 TCB 仍可能处于原状态列表，按地址去重并覆盖待就绪状态。
            await AddListAsync(TaskGlobal("xPendingReadyList"), "PendingReady", true);
            if (current is > 0 && ValidPointer(current.Value, "当前任务") && !states.ContainsKey(current.Value))
            {
                states[current.Value] = "Unknown";
                diagnostics.Add("当前 TCB 不在可读取的任务列表中，可能暂停在任务状态转换途中。");
            }

            var hasEnd = await HasMemberAsync("TCB_t", "pxEndOfStack");
            var hasBasePriority = await HasMemberAsync("TCB_t", "uxBasePriority");
            var hasRuntime = await HasMemberAsync("TCB_t", "ulRunTimeCounter");
            var hasTrace = await HasMemberAsync("TCB_t", "uxTCBNumber");
            if (!hasEnd) diagnostics.Add("TCB 未记录栈末地址，栈容量和水位不可用。可在固件启用 configRECORD_STACK_HIGH_ADDRESS=1 后重新编译。");
            var direction = stackGrowsDown;
            if (direction is null)
            {
                var growth = await EvaluateAsync("(long long)(portSTACK_GROWTH)", "栈增长方向", false);
                if (long.TryParse(growth, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signed) && signed != 0)
                    direction = signed < 0;
                else diagnostics.Add("未确认端口栈增长方向，栈水位不可用；保留 -g3 宏信息可帮助核对。");
            }
            var stackFilled = hasTrace;
            if (!stackFilled)
            {
                var overflow = await NumberAsync("configCHECK_FOR_STACK_OVERFLOW", "栈填充设置", false);
                var trace = await NumberAsync("configUSE_TRACE_FACILITY", "栈填充设置", false);
                var watermark = await NumberAsync("INCLUDE_uxTaskGetStackHighWaterMark", "栈填充设置", false);
                var watermark2 = await NumberAsync("INCLUDE_uxTaskGetStackHighWaterMark2", "栈填充设置", false);
                stackFilled = overflow > 1 || trace == 1 || watermark == 1 || watermark2 == 1;
            }
            if (!stackFilled) diagnostics.Add("未确认任务栈按 A5 填充，栈水位不可用；可启用 configUSE_TRACE_FACILITY=1 或栈水位检查，并保留 -g3 宏信息。");

            var result = new List<FreeRtosTask>();
            foreach (var item in states)
            {
                token.ThrowIfCancellationRequested();
                if (!ValidPointer(item.Key, "TCB")) continue;
                var expression = Pointer("TCB_t", item.Key);
                var label = "任务 0x" + item.Key.ToString("x", CultureInfo.InvariantCulture);
                var name = await StringAsync(expression + "->pcTaskName", label + " 名称") ?? label;
                var priority = await NumberAsync(expression + "->uxPriority", name + " 优先级");
                var basePriority = hasBasePriority ? await NumberAsync(expression + "->uxBasePriority", name + " 基础优先级") : null;
                var runtime = hasRuntime ? await NumberAsync(expression + "->ulRunTimeCounter", name + " 运行计数") : null;
                var stack = await NumberAsync(expression + "->pxStack", name + " 栈起始");
                var top = await NumberAsync(expression + "->pxTopOfStack", name + " 保存的栈指针");
                var end = hasEnd ? await NumberAsync(expression + "->pxEndOfStack", name + " 栈末地址") : null;
                var wordSize = stack is > 0 && end is > 0 ? await NumberAsync("sizeof(*" + expression + "->pxStack)", name + " 栈元素大小") : null;
                ulong? capacity = null;
                if (stack is > 0 && end is > 0 && wordSize is > 0 and <= 16 && end >= stack &&
                    end <= ulong.MaxValue - wordSize && (pointerSize != 4 || end + wordSize <= uint.MaxValue) &&
                    end.Value - stack.Value <= MaximumStackBytes - wordSize)
                {
                    capacity = end.Value - stack.Value + wordSize.Value;
                    if (top is not null && (top < stack || top > end)) diagnostics.Add(name + " 保存的栈指针超出记录的栈范围；可能发生溢出或暂停在上下文切换中。");
                }
                else if (end is not null) diagnostics.Add(name + " 栈边界异常或超出单次安全读取范围，未扫描。");
                var highWater = capacity is > 0 && direction is not null && stackFilled
                    ? await ReadStackWaterAsync(stack!.Value, end!.Value, capacity.Value, wordSize!.Value, direction.Value, name)
                    : null;
                var state = item.Value;
                if (state == "Suspended") state = await SuspendedStateAsync(expression, name);
                if (schedulerRunning && item.Key == current && state != "Deleted") state = "Running";
                result.Add(new(item.Key, name, state, priority, basePriority, stack, top, highWater, capacity, runtime));
            }
            if (reportedCount is not null && (ulong)result.Count != reportedCount)
                diagnostics.Add("内核报告 " + reportedCount + " 个任务，但读取了 " + result.Count + " 个不同 TCB；快照可能不完整。");
            if (schedulerRunning) diagnostics.Add("Running 标记表示调度器当前选择的 TCB；暂停在中断中时不代表任务正在执行指令。栈指针是 TCB 保存值，不替代当前 CPU SP。");
            if (hasEnd) diagnostics.Add("栈容量按 TCB 的记录边界计算；端口顶部对齐可能保留额外字节，不替代创建任务时申请的栈长度。");
            return result.ToArray();

            async Task AddListAsync(string expression, string state, bool optional)
            {
                foreach (var owner in await ListOwnersAsync(expression, expression, optional))
                    if (!states.ContainsKey(owner) || state == "PendingReady") states[owner] = state;
            }
        }

        private async Task<ulong[]> ListOwnersAsync(string expression, string label, bool optional)
        {
            var count = await NumberAsync(expression + ".uxNumberOfItems", label + " 长度", !optional);
            if (count is null || count == 0) return [];
            if (count > MaximumListEntries)
            {
                diagnostics.Add(label + " 计数异常，未遍历：" + count);
                return [];
            }
            var listAddress = await NumberAsync("&(" + expression + ")", label + " 地址");
            var end = await NumberAsync("&(" + expression + ".xListEnd)", label + " 哨兵");
            var node = await NumberAsync(expression + ".xListEnd.pxNext", label + " 首项");
            if (listAddress is null || end is null || node is null) return [];
            var visited = new HashSet<ulong>();
            var owners = new List<ulong>();
            for (ulong index = 0; index < count; index++)
            {
                if (node == end || !ValidPointer(node.Value, label + " 列表项"))
                {
                    diagnostics.Add(label + " 链表比计数短，可能暂停在更新中途。");
                    return owners.ToArray();
                }
                if (!visited.Add(node.Value))
                {
                    diagnostics.Add(label + " 检测到非哨兵循环，停止读取该链表。");
                    return owners.ToArray();
                }
                var item = Pointer("ListItem_t", node.Value);
                if (listContainerField.Length > 0)
                {
                    var container = await NumberAsync(item + "->" + listContainerField, label + " 容器");
                    if (container != listAddress)
                    {
                        diagnostics.Add(label + " 列表项容器不匹配，快照不完整。");
                        return owners.ToArray();
                    }
                }
                var owner = await NumberAsync(item + "->pvOwner", label + " 所有者");
                if (owner is > 0 && ValidPointer(owner.Value, label + " TCB")) owners.Add(owner.Value);
                node = await NumberAsync(item + "->pxNext", label + " 下一项");
                if (node is null) return owners.ToArray();
            }
            if (node != end) diagnostics.Add(label + " 遍历数量与哨兵不一致，快照可能不完整。");
            return owners.ToArray();
        }

        private async Task<string> SuspendedStateAsync(string expression, string name)
        {
            if (listContainerField.Length == 0)
            {
                diagnostics.Add(name + " 缺少事件列表容器字段，无法区分显式挂起与无限期等待。");
                return "SuspendedOrBlocked";
            }
            var container = await NumberAsync(expression + "->xEventListItem." + listContainerField, name + " 事件等待");
            if (container is null) return "SuspendedOrBlocked";
            if (container > 0) return "Blocked";
            // 新内核的通知数组与旧内核的单通知字段都不进行函数调用。
            if (await HasMemberAsync("TCB_t", "ucNotifyState"))
            {
                var arrayCount = await NumberAsync("sizeof(" + expression + "->ucNotifyState)/sizeof(" + expression + "->ucNotifyState[0])", name + " 通知数", false);
                if (arrayCount is > 0 and <= MaximumArrayEntries)
                {
                    for (ulong i = 0; i < arrayCount; i++)
                    {
                        var state = await NumberAsync(expression + "->ucNotifyState[" + i.ToString(CultureInfo.InvariantCulture) + "]", name + " 通知状态");
                        if (state == 1) return "Blocked";
                        if (state is null) return "SuspendedOrBlocked";
                    }
                }
                else
                {
                    var state = await NumberAsync(expression + "->ucNotifyState", name + " 通知状态");
                    if (state == 1) return "Blocked";
                    if (state is null) return "SuspendedOrBlocked";
                }
            }
            return "Suspended";
        }

        private async Task<ulong?> ReadStackWaterAsync(ulong start, ulong end, ulong bytes, ulong wordSize, bool down, string name)
        {
            ulong unused = 0;
            try
            {
                while (unused < bytes)
                {
                    token.ThrowIfCancellationRequested();
                    var count = (int)Math.Min(256UL, bytes - unused);
                    // 向上增长端口从最高一个 StackType_t 的末字节反向扫描。
                    var address = down ? start + unused : end + wordSize - unused - (ulong)count;
                    var result = await SendReadAsync("-data-read-memory-bytes 0x" + address.ToString("x", CultureInfo.InvariantCulture) + " " + count.ToString(CultureInfo.InvariantCulture));
                    var blocks = result.Get("memory")?.Values.ToArray() ?? [];
                    if (blocks.Length != 1 || blocks[0].Get("contents")?.Text is not { } contents || contents.Length != count * 2 || !contents.All(Uri.IsHexDigit) ||
                        !TryMemoryAddress(blocks[0].String("begin"), out var returnedAddress) || returnedAddress != address ||
                        !TryMemoryAddress(blocks[0].String("end"), out var returnedEnd) || address > ulong.MaxValue - (ulong)count || returnedEnd != address + (ulong)count)
                        throw new StudioXException("GDB_PROTOCOL", "RTOS 栈读取返回无效的内存块。");
                    var data = Convert.FromHexString(contents);
                    for (var i = 0; i < count; i++)
                    {
                        if (data[down ? i : count - 1 - i] != 0xa5) return unused / wordSize * wordSize;
                        unused++;
                    }
                }
                return unused / wordSize * wordSize;
            }
            catch (StudioXException ex) when (ex.Code == "GDB_COMMAND")
            {
                diagnostics.Add(name + " 栈水位读取失败：" + ex.Message);
                return null;
            }
        }

        private static bool TryMemoryAddress(string value, out ulong address)
        {
            address = 0;
            return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                ulong.TryParse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
        }
    }
}
