namespace StudioX.Application;

using System.Buffers.Binary;
using System.Globalization;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class DebugSessionService
{
    public async Task<FaultEvidence> ReadFaultEvidenceAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            if (!IsHardware || HardwareTarget?.Core is not ("Cortex-M3" or "Cortex-M4" or "Cortex-M7"))
            {
                throw new StudioXException("FAULT_TARGET", "只读现场采集需要已校验固件且暂停的 Cortex-M3/M4/M7 实机会话。");
            }
            var bytes = Convert.FromHexString(await adapter!.ReadMemoryAsync(0xe000ed28, 20, token));
            if (bytes.Length != 20)
            {
                throw new StudioXException("FAULT_READ", "故障寄存器响应不完整。");
            }
            var cfsr = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            var raw = string.Join("\n", Snapshot.Registers.Select(r => r.Name + "=" + r.Value));
            // 暂停位置可能已执行异常处理函数序言；不能把当前 MSP/PSP 猜成异常入口帧。
            raw += "\n当前 GDB 调用栈：\n" + string.Join("\n", Snapshot.Frames.Select(f => $"{f.Address} {f.Function} {f.File}:{f.Line}"));
            raw += "\n异常入口 SP 未核实，未从当前栈猜测故障前 PC；可导入固件显式保存的异常现场。";
            var addresses = Snapshot.Frames.Select(f => Parse(f.Address)).OfType<uint>().Take(32).ToArray();
            return new(1, HardwareTarget.DeviceId, "已暂停的实机会话；SCB 只读采集", DateTimeOffset.UtcNow, cfsr,
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)), null, null, raw, true, addresses);
        }
        finally { gate.Release(); }

        static uint? Parse(string? value)
        {
            return value is not null && value.StartsWith("0x", StringComparison.Ordinal) && uint.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) ? number : null;
        }
    }
}
