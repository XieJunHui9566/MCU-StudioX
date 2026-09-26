namespace StudioX.Engine.Debugging;

using System.Globalization;

public sealed partial class FreeRtosInspector
{
    private sealed partial class Reader
    {
        private async Task<FreeRtosObject[]> ReadObjectsAsync(string[] symbols)
        {
            var names = new Dictionary<ulong, string>();
            var registry = await NumberAsync("sizeof(" + QueueGlobal("xQueueRegistry") + ")/sizeof(" + QueueGlobal("xQueueRegistry[0]") + ")", "对象注册表", false);
            if (registry is > 0 and <= MaximumArrayEntries)
            {
                for (ulong index = 0; index < registry; index++)
                {
                    var expression = QueueGlobal("xQueueRegistry[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                    var namePointer = await NumberAsync(expression + ".pcQueueName", "对象注册表名称指针");
                    // 内核解除注册会把名称置空，旧句柄可能留在已空的注册表槽中。
                    if (namePointer is null or 0) continue;
                    var address = await NumberAsync(expression + ".xHandle", "对象注册表句柄");
                    if (address is > 0 && ValidPointer(address.Value, "注册对象"))
                    {
                        var name = await StringAsync(expression + ".pcQueueName", "对象注册表名称");
                        names.TryAdd(address.Value, name ?? "已注册对象 0x" + address.Value.ToString("x", CultureInfo.InvariantCulture));
                    }
                }
            }
            else
            {
                diagnostics.Add(registry is null or 0
                    ? "对象注册表不可用；不会认为工程没有队列或信号量。可添加全局句柄观察，或配置 configQUEUE_REGISTRY_SIZE 并用 vQueueAddToRegistry 注册对象。"
                    : "对象注册表长度异常，未遍历：" + registry);
            }
            foreach (var symbol in symbols)
            {
                // sizeof(*handle) 确认它是指针；成员必须来自已知 Queue_t 类型，禁止把任意整数当地址读取。
                var globalSymbol = "::" + symbol;
                var typed = await NumberAsync("sizeof((" + globalSymbol + ")->uxMessagesWaiting)", symbol + " 句柄类型");
                if (typed is null) continue;
                var address = await NumberAsync(globalSymbol, symbol + " 句柄");
                if (address is null) continue;
                if (address == 0) { diagnostics.Add(symbol + " 句柄为空，对象尚未创建或已经清除。"); continue; }
                if (ValidPointer(address.Value, symbol + " 句柄")) names.TryAdd(address.Value, symbol);
            }
            if (names.Count == 0) return [];
            var hasType = await HasMemberAsync("Queue_t", "ucQueueType");
            var hasQueueSets = await HasMemberAsync("Queue_t", "pxQueueSetContainer");
            if (!hasType) diagnostics.Add("队列类型字段未保留；会区分互斥锁与信号量，但不会猜测递归互斥锁或二值/计数信号量。");
            var result = new List<FreeRtosObject>();
            foreach (var item in names)
            {
                var expression = Pointer("Queue_t", item.Key);
                var count = await NumberAsync(expression + "->uxMessagesWaiting", item.Value + " 当前计数");
                var capacity = await NumberAsync(expression + "->uxLength", item.Value + " 容量");
                var size = await NumberAsync(expression + "->uxItemSize", item.Value + " 项字节数");
                if (count is null && capacity is null && size is null) continue;
                var kind = "Unknown";
                var type = hasType ? await NumberAsync(expression + "->ucQueueType", item.Value + " 类型") : null;
                if (type is not null) kind = type switch
                {
                    0 => "Queue", 1 => "Mutex", 2 => "CountingSemaphore", 3 => "BinarySemaphore", 4 => "RecursiveMutex", _ => "Unknown"
                };
                // 官方 queueQUEUE_TYPE_SET 与普通队列同为 0，不从 itemSize 或名字猜测队列集。
                if (type == 0 && hasQueueSets)
                {
                    kind = "QueueOrQueueSet";
                    diagnostics.Add(item.Value + " 的内核类型 0 同时用于普通队列和队列集，显示统一队列状态。");
                }
                else if (type is null && size is > 0) kind = hasQueueSets ? "QueueOrQueueSet" : "Queue";
                else if (type is null && size == 0)
                {
                    var head = await NumberAsync(expression + "->pcHead", item.Value + " 同步类型标记");
                    if (head is not null) kind = head == 0 ? "Mutex" : "Semaphore";
                }
                var send = await NumberAsync(expression + "->xTasksWaitingToSend.uxNumberOfItems", item.Value + " 等待发送任务数");
                var receive = await NumberAsync(expression + "->xTasksWaitingToReceive.uxNumberOfItems", item.Value + " 等待接收任务数");
                ulong? owner = null, recursion = null;
                if (kind is "Mutex" or "RecursiveMutex")
                {
                    owner = await NumberAsync(expression + "->u.xSemaphore.xMutexHolder", item.Value + " 互斥锁持有者");
                    recursion = await NumberAsync(expression + "->u.xSemaphore.uxRecursiveCallCount", item.Value + " 递归层数");
                }
                if (count > capacity) diagnostics.Add(item.Value + " 对象计数超过容量，可能暂停在对象更新中途或句柄类型不符。");
                result.Add(new(item.Key, item.Value, kind, count, capacity, size, send, receive, owner, recursion));
            }
            return result.ToArray();
        }
    }
}
