namespace StudioX.Application.Mcp;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class StudioXMcpTools
{
    [McpServerTool(Name = "debug_rtos_snapshot")]
    [Description("只读读取当前工程已暂停调试目标的 FreeRTOS 任务、调度器、堆和队列/信号量/互斥量状态。根据 ELF 内核符号与类型读取，不调用目标函数或修改内存；可选指定未注册的全局 QueueHandle_t 符号。不可读取的字段保留为空并提供诊断，空对象列表不代表工程没有对象。")]
    public async Task<string> DebugRtosSnapshotAsync(
        [Description("可选的全局 QueueHandle_t / SemaphoreHandle_t 符号列表，如 [\"sensorQueue\",\"app.busMutex\"]。仅接受 C 标识符及点分隔成员路径，不接受函数调用、指针表达式或地址；已注册对象自动读取。")]
        string[]? objectSymbols = null,
        CancellationToken cancellationToken = default)
    {
        await mcpDebugGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var debug = RequireMatchingDebugProject();
            if (debug.State != DebugState.Stopped)
                throw new StudioXException("DEBUG_STATE", "暂停目标后才能读取 FreeRTOS 状态。");
            var snapshot = debug.Snapshot;
            var hardware = debug.IsHardware;
            var result = await debug.ReadFreeRtosAsync(objectSymbols, cancellationToken).ConfigureAwait(false);
            // 调试器由 IDE 和 MCP 共用；旧工程或旧暂停位置的响应不能当作当前 RTOS 状态。
            if (!DebugProjectMatches(debug) || debug.State != DebugState.Stopped ||
                !ReferenceEquals(snapshot, debug.Snapshot))
                throw new StudioXException("DEBUG_SESSION_CHANGED", "读取 FreeRTOS 状态期间调试会话已改变，请重新读取状态后再操作。");
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            options.Converters.Add(new JsonStringEnumConverter());
            return JsonSerializer.Serialize(new
            {
                project = Project,
                state = debug.State.ToString(),
                snapshot = result,
                hardware,
                simulated = !hardware,
                evidence = hardware
                    ? "GDB 返回的已暂停目标内核符号和内存；不是持续运行轨迹。"
                    : "离线调试数据；演示不证明芯片上的任务执行或堆状态。"
            }, options);
        }
        finally { mcpDebugGate.Release(); }
    }
}
