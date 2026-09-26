namespace StudioX.Application;

using StudioX.Engine.Debugging;

public sealed partial class DebugSessionService
{
    /// <summary>与寄存器、单步和停止共用会话锁；仅暂停时以符号和内存读取内核状态。</summary>
    public async Task<FreeRtosSnapshot> ReadFreeRtosAsync(IReadOnlyList<string>? objectSymbols = null, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            // 检查 DWARF 中的内部类型，不执行 uxTaskGetSystemState / vPortGetHeapStats 等目标函数。
            var stackGrowsDown = HardwareTarget is { } target &&
                (target.IsWch || target.IsRp2350 || target.Core.StartsWith("Cortex-M", StringComparison.Ordinal)) ? true : (bool?)null;
            return await new FreeRtosInspector(adapter!, stackGrowsDown).ReadAsync(objectSymbols, token);
        }
        finally { gate.Release(); }
    }
}
