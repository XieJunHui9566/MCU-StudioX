namespace StudioX.Application;

using System.Globalization;
using StudioX.Foundation;

public sealed partial class DebugSessionService
{
    public async Task<DebugDisassembly> ReadDisassemblyAsync(uint? address = null, int byteCount = 128, CancellationToken token = default)
    {
        if (byteCount is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(byteCount), "单次反汇编必须为 1–512 字节。");
        if (address is { } requested) ValidateDisassemblyRange(requested, byteCount);
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            // ReadAsync 的寄存器来自 frame 0；不能重新求值选中调用者的 $pc 冒充实际执行位置。
            var pc = ParseDebugAddress(Snapshot.Registers.FirstOrDefault(register => register.Name == "pc")?.Value)
                ?? ParseDebugAddress(Snapshot.Frames.FirstOrDefault(frame => frame.Level == 0)?.Address);
            var start = address ?? pc ?? throw new StudioXException("DEBUG_DISASSEMBLY", "GDB 未返回可用的 PC，请手动输入十六进制地址。");
            var end = ValidateDisassemblyRange(start, byteCount);
            var instructions = await adapter!.ReadDisassemblyAsync(start, byteCount, token);
            return new(start, end, start, pc, instructions);
        }
        finally { gate.Release(); }
    }

    private static uint ValidateDisassemblyRange(uint address, int byteCount)
    {
        var end = (ulong)address + (uint)byteCount;
        if (end > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(address), "反汇编范围超出可表示的 32 位地址。");
        return (uint)end;
    }

    private static uint? ParseDebugAddress(string? text) => text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address) ? address : null;
}
