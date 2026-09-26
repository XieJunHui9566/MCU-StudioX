namespace StudioX.Engine.Debugging;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>通过 DWARF 类型读取 FreeRTOS；只使用 MI 读取命令，不执行目标内核函数。</summary>
public sealed partial class FreeRtosInspector(GdbDebugAdapter adapter, bool? stackGrowsDown = null)
{
    public async Task<FreeRtosSnapshot> ReadAsync(IReadOnlyList<string>? objectSymbols = null, CancellationToken token = default)
    {
        var symbols = objectSymbols?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        foreach (var symbol in symbols)
            if (!IsObjectSymbol(symbol)) throw new ArgumentException("RTOS 对象必须是全局句柄名称或点成员路径，不接受函数、下标、赋值或地址表达式。", nameof(objectSymbols));
        return await new Reader(adapter, stackGrowsDown, token).ReadAsync(symbols);
    }

    public static bool IsObjectSymbol(string? symbol) => symbol is { Length: > 0 and <= 256 } &&
        Regex.IsMatch(symbol, @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z", RegexOptions.CultureInvariant);

    private sealed partial class Reader(GdbDebugAdapter adapter, bool? stackGrowsDown, CancellationToken token)
    {
        // 这些是单次坏链保护，不限制调试会话或模型的工具调用次数。
        private const ulong MaximumListEntries = 4096;
        private const ulong MaximumArrayEntries = 1024;
        private const ulong MaximumStackBytes = 1024 * 1024;
        private readonly List<string> diagnostics = [];
        private readonly Dictionary<string, ulong?> numbers = new(StringComparer.Ordinal);
        private string taskScope = "";
        private string queueScope = "";
        private string heapScope = "";
        private ulong pointerSize;

        public async Task<FreeRtosSnapshot> ReadAsync(string[] symbols)
        {
            token.ThrowIfCancellationRequested();
            taskScope = await ScopeAsync("tasks.c", "pxReadyTasksLists");
            queueScope = await ScopeAsync("queue.c", "xQueueRegistry");
            var smp = await NumberAsync("sizeof(" + TaskGlobal("pxCurrentTCBs") + ")", "SMP", false);
            if (smp is not null)
            {
                diagnostics.Add("检测到多核 FreeRTOS pxCurrentTCBs；当前软件仅解析经典单核布局，不推断多核任务状态。");
                return new(true, null, null, null, null, null, null, [], null, [], diagnostics.ToArray());
            }
            var count = await NumberAsync(TaskGlobal("uxCurrentNumberOfTasks"), "任务数");
            var current = await NumberAsync(TaskGlobal("pxCurrentTCB"), "当前任务");
            if (count is null && current is null)
            {
                diagnostics.Add("没有找到可读取的经典 FreeRTOS 内核符号。请加载保留 -g 调试类型信息的固件 ELF；裸机工程没有 RTOS 状态。");
                return new(false, null, null, null, null, null, null, [], null, [], diagnostics.ToArray());
            }
            pointerSize = await NumberAsync("sizeof(void*)", "目标指针宽度") ?? 0;
            if (pointerSize is not (4 or 8))
                throw new StudioXException("DEBUG_RTOS_LAYOUT", "无法确认目标指针宽度，不解析内核数据。");
            var currentLayout = await NumberAsync("sizeof(" + TaskGlobal("pxCurrentTCB") + ")", "当前任务符号布局", false);
            if (currentLayout is not null && currentLayout != pointerSize)
            {
                diagnostics.Add("pxCurrentTCB 不是经典单核指针布局（可能为旧版 SMP 数组），当前软件不解析该布局。");
                return new(true, null, null, null, null, null, count, [], null, [], diagnostics.ToArray());
            }
            var scheduler = await NumberAsync(TaskGlobal("xSchedulerRunning"), "调度器运行状态");
            var invalidKernel = false;
            if (scheduler is > 1)
            {
                diagnostics.Add("调度器标志必须是 0 或 1，读取值为 " + scheduler + "；未确认有效 FreeRTOS 内核状态。");
                invalidKernel = true;
            }
            if (count > MaximumListEntries)
            {
                diagnostics.Add("内核任务数 " + count + " 超出单次链表安全读取范围，不能作为可信任务统计。");
                invalidKernel = true;
            }
            if (current is > 0 && !ValidPointer(current.Value, "当前任务")) invalidKernel = true;
            if (count == 0 && current is > 0)
            {
                diagnostics.Add("内核报告没有任务，但当前 TCB 非空，关键内核字段相互矛盾。");
                invalidKernel = true;
            }
            if (scheduler == 1 && (current == 0 || count == 0))
            {
                diagnostics.Add("调度器运行标志为 1，但当前 TCB 为空或任务数为零，未确认可解释的运行内核。");
                invalidKernel = true;
            }
            if (invalidKernel)
            {
                // ELF 中有内核符号不等于目标正在运行这份固件；垃圾 RAM 不能显示成实时 RTOS 统计。
                diagnostics.Add("关键字段校验失败，停止读取任务、堆和对象。请核对 ELF 与目标固件是否匹配，并检查内核 RAM 是否受损。");
                return new(false, null, null, null, null, null, null, [], null, [], diagnostics.ToArray());
            }
            var suspended = await NumberAsync(TaskGlobal("uxSchedulerSuspended"), "调度器挂起层数");
            if (suspended > MaximumListEntries)
            {
                diagnostics.Add("调度器挂起层数 " + suspended + " 超出可解释的嵌套范围，该字段不可用。");
                suspended = null;
            }
            var ticks = await NumberAsync(TaskGlobal("xTickCount"), "Tick");
            var version = await StringAsync("tskKERNEL_VERSION_NUMBER", "内核版本", false);
            if (version is null) diagnostics.Add("内核版本宏未保留在调试信息中，版本不可用；不会根据字段布局猜测版本。");
            var tasks = await ReadTasksAsync(current, scheduler != 0 && scheduler is not null, count);
            var heap = await ReadHeapAsync();
            var objects = await ReadObjectsAsync(symbols);
            token.ThrowIfCancellationRequested();
            diagnostics.Add("数据来自暂停瞬间；若暂停在内核链表更新途中，可能出现不完整快照。不会调用目标函数或改变任务调度。");
            return new(true, version, scheduler is null ? null : scheduler != 0, suspended, ticks, current, count,
                tasks, heap, objects, diagnostics.Distinct(StringComparer.Ordinal).ToArray());
        }

        private async Task<string> ScopeAsync(string file, string symbol)
        {
            var prefix = "'" + file + "'::";
            // :: 保证用户选择调用方栈帧时，同名局部变量不会遮蔽请求的内核全局符号。
            return await NumberAsync("sizeof(" + prefix + symbol + ")", file + " 作用域", false) is not null ? prefix : "::";
        }

        private string TaskGlobal(string expression) => taskScope + expression;
        private string QueueGlobal(string expression) => queueScope + expression;
        private string HeapGlobal(string expression) => heapScope + expression;
        private static string Pointer(string type, ulong address) => "((" + type + "*)0x" + address.ToString("x", CultureInfo.InvariantCulture) + ")";

        private async Task<string?> EvaluateAsync(string expression, string label, bool report)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await SendReadAsync("-data-evaluate-expression " + MiRecord.Quote(expression));
                var values = result.Children.Where(x => x.Key == "value").ToArray();
                if (values.Length != 1 || values[0].Value.Text is not { } value)
                    throw new StudioXException("GDB_PROTOCOL", "RTOS 读取响应缺少唯一的 value 字段：" + label);
                return value;
            }
            catch (StudioXException ex) when (ex.Code == "GDB_COMMAND")
            {
                if (report) diagnostics.Add(label + " 不可用：" + ex.Message);
                return null;
            }
        }

        private async Task<MiValue> SendReadAsync(string command)
        {
            token.ThrowIfCancellationRequested();
            // MI 在途命令必须读完响应；把 UI 取消令牌交给 transport 会使它丢弃响应并终止整个硬件会话。
            // 取消只在命令边界生效，单条命令仍受 transport 自身的超时保护。
            var result = await adapter.SendAsync(command, CancellationToken.None);
            token.ThrowIfCancellationRequested();
            return result;
        }

        private async Task<ulong?> NumberAsync(string expression, string label, bool report = true)
        {
            if (numbers.TryGetValue(expression, out var cached)) return cached;
            var value = await EvaluateAsync("(unsigned long long)(" + expression + ")", label, report);
            if (value is null) { numbers[expression] = null; return null; }
            var text = value.Trim();
            var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (!ulong.TryParse(hex ? text.AsSpan(2) : text.AsSpan(), hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new StudioXException("GDB_PROTOCOL", "RTOS 数值格式无效（" + label + "）：" + value);
            numbers[expression] = number;
            return number;
        }

        private async Task<string?> StringAsync(string expression, string label, bool report = true)
        {
            var value = await EvaluateAsync(expression, label, report);
            if (value is null) return null;
            var start = value.IndexOf('"');
            if (start >= 0)
            {
                var escaped = false;
                for (var i = start + 1; i < value.Length; i++)
                {
                    if (!escaped && value[i] == '"')
                    {
                        var result = MiRecord.Parse("^done,value=" + value[start..(i + 1)]).Data.String("value");
                        var nul = result.IndexOf('\0');
                        return nul >= 0 ? result[..nul] : result;
                    }
                    if (!escaped && value[i] == '\\') escaped = true;
                    else escaped = false;
                }
            }
            if (report) diagnostics.Add(label + " 没有可读取的字符串：" + value);
            return null;
        }

        private bool ValidPointer(ulong value, string label)
        {
            if (value != 0 && value % pointerSize == 0 && (pointerSize != 4 || value <= uint.MaxValue)) return true;
            diagnostics.Add(label + " 包含空、未对齐或超出目标指针宽度的地址：0x" + value.ToString("x", CultureInfo.InvariantCulture));
            return false;
        }

        private async Task<bool> HasMemberAsync(string type, string field) =>
            await NumberAsync("sizeof(((" + type + "*)0)->" + field + ")", type + "." + field, false) is not null;
    }
}
