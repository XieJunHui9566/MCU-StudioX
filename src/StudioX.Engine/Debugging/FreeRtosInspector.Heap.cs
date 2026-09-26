namespace StudioX.Engine.Debugging;

using System.Globalization;

public sealed partial class FreeRtosInspector
{
    private sealed partial class Reader
    {
        private async Task<FreeRtosHeap?> ReadHeapAsync()
        {
            heapScope = await ScopeAsync("heap_4.c", "xFreeBytesRemaining");
            if (heapScope == "::") heapScope = await ScopeAsync("heap_5.c", "xFreeBytesRemaining");
            var free = await NumberAsync(HeapGlobal("xFreeBytesRemaining"), "FreeRTOS 堆可用字节");
            var end = await NumberAsync(HeapGlobal("pxEnd"), "heap_4/5 结束块");
            var first = await NumberAsync(HeapGlobal("xStart.pxNextFreeBlock"), "heap_4/5 首个空闲块");
            if (free is null || end is null || first is null)
            {
                diagnostics.Add("未找到完整的 heap_4/heap_5 空闲链表符号，堆布局未确认；heap_1/2/3 或用户分配器暂不解析。");
                return null;
            }
            var total = await NumberAsync("sizeof(" + HeapGlobal("ucHeap") + ")", "堆数组容量", false);
            var isHeap5 = heapScope == "'heap_5.c'::";
            // 没有 ucHeap 不能唯一证明 heap_5，可能是优化丢失或自定义兼容分配器。
            var kind = isHeap5 ? "heap_5" : total is not null ? "heap_4" : "heap_4/5";
            if (total is null) diagnostics.Add("堆总容量不可用；heap_5 的多个区域及被优化掉的 ucHeap 不从空闲字节数推断容量。");
            var minimum = await NumberAsync(HeapGlobal("xMinimumEverFreeBytesRemaining"), "堆历史最少可用");
            var allocations = await OptionalHeapNumberAsync("xNumberOfSuccessfulAllocations", "成功分配次数");
            var frees = await OptionalHeapNumberAsync("xNumberOfSuccessfulFrees", "成功释放次数");
            if (total is not null && free > total)
            {
                diagnostics.Add("堆可用字节 " + free + " 超过数组总容量 " + total + "，堆统计无效；未把该值作为可信使用量，也不继续读取空闲块。");
                return new(kind, total, null, null, null, null, null, null);
            }
            if (total is not null && minimum > total)
            {
                diagnostics.Add("堆历史最低可用量 " + minimum + " 超过数组总容量 " + total + "，该字段不可用。");
                minimum = null;
            }
            else if (minimum > free)
            {
                diagnostics.Add("堆历史最低可用量 " + minimum + " 大于当前可用量 " + free + "，可能暂停在堆统计更新中途，该字段不可用。");
                minimum = null;
            }
            if (end == 0)
            {
                diagnostics.Add("FreeRTOS 堆尚未初始化，不把空闲链表的初始零值当作真实分配统计。");
                return new(kind, total, null, null, null, null, null, null);
            }
            var hasCanary = await NumberAsync("sizeof(" + HeapGlobal("xHeapCanary") + ")", "堆保护字段", false) is not null;
            var canaryValue = hasCanary ? await NumberAsync(HeapGlobal("xHeapCanary"), "堆指针保护值") : null;
            if (hasCanary && canaryValue is null)
            {
                diagnostics.Add("堆保护字段存在但保护值无法读取，未把编码指针当作普通地址遍历。");
                return new(kind, total, free, minimum, allocations, frees, null, null);
            }
            var canary = canaryValue ?? 0;
            var node = first.Value ^ canary;
            ulong? heapStart = total is not null ? await NumberAsync("&(" + HeapGlobal("ucHeap") + ")", "堆范围起始") : null;
            ulong? heapEnd = heapStart is not null && total is not null && heapStart <= ulong.MaxValue - total ? heapStart + total : null;
            ulong sum = 0, largest = 0, blocks = 0;
            var visited = new HashSet<ulong>();
            var headerSize = await NumberAsync("sizeof(BlockLink_t)", "堆块头大小");
            var valid = headerSize is > 0 && ValidPointer(end.Value, "堆结束块");
            while (valid && node != end)
            {
                token.ThrowIfCancellationRequested();
                if (!ValidPointer(node, "堆空闲块") || !visited.Add(node) || (ulong)visited.Count > MaximumListEntries)
                {
                    diagnostics.Add("堆空闲链表含循环、无效地址或异常数量，未提供碎片统计。");
                    valid = false;
                    break;
                }
                if (heapStart is not null && heapEnd is not null && (node < heapStart || node > heapEnd || headerSize > heapEnd - node))
                {
                    diagnostics.Add("堆空闲块超出 ucHeap 边界，未读取该地址。");
                    valid = false;
                    break;
                }
                var expression = Pointer("BlockLink_t", node);
                var size = await NumberAsync(expression + "->xBlockSize", "堆空闲块大小");
                var next = await NumberAsync(expression + "->pxNextFreeBlock", "堆下一空闲块");
                if (size is null || next is null) { valid = false; break; }
                var decoded = next.Value ^ canary;
                // heap_5 的区域结束哨兵 size=0；它们不是可分配空闲块。
                if (size > 0)
                {
                    if (size < headerSize || size > free || sum > ulong.MaxValue - size ||
                        heapEnd is not null && size > heapEnd - node)
                    {
                        diagnostics.Add("堆空闲块大小或累计统计异常，未提供碎片统计。");
                        valid = false;
                        break;
                    }
                    sum += size.Value;
                    largest = Math.Max(largest, size.Value);
                    blocks++;
                }
                if (decoded <= node)
                {
                    diagnostics.Add("堆空闲块地址未递增或提前结束，快照可能不完整。");
                    valid = false;
                    break;
                }
                node = decoded;
            }
            if (valid && sum != free)
            {
                diagnostics.Add("空闲块合计 " + sum.ToString(CultureInfo.InvariantCulture) + " 字节与内核统计 " + free + " 不一致，碎片统计不可用。");
                valid = false;
            }
            return new(kind, total, free, minimum, allocations, frees, valid ? largest : null, valid ? blocks : null);
        }

        private async Task<ulong?> OptionalHeapNumberAsync(string field, string label) =>
            await NumberAsync("sizeof(" + HeapGlobal(field) + ")", label + " 字段", false) is not null
                ? await NumberAsync(HeapGlobal(field), label)
                : null;
    }
}
