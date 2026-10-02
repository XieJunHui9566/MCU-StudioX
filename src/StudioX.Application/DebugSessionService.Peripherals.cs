namespace StudioX.Application;

using StudioX.Engine.Debugging;
using StudioX.Engine.Svd;

public sealed partial class DebugSessionService
{
    // 每次状态变更使旧读数和待确认写入失效，包括运行后再次暂停到同一会话。
    public long PeripheralRevision { get; private set; }
    private string? peripheralDeviceId;
    public async Task<ulong> ReadPeripheralAsync(string deviceId, SvdRegister register, long revision, bool acknowledgeSideEffects, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequirePeripheral(deviceId, register, revision);
            if (!register.CanRead || (register.HasReadSideEffects && !acknowledgeSideEffects)) { throw Error("寄存器不可读，或需要单独确认读取副作用。"); }
            await adapter!.SendAsync("-interpreter-exec console \"maintenance flush dcache\"", token);
            var bytes = Convert.FromHexString(await adapter.ReadMemoryAsync(register.Address, register.Width / 8, token));
            if (bytes.Length != register.Width / 8) { throw Error("寄存器返回的数据长度不完整。"); }
            if (!plotLittleEndian) { Array.Reverse(bytes); }
            ulong value = 0;
            for (var i = 0; i < bytes.Length; i++) { value |= (ulong)bytes[i] << (i * 8); }
            Trace($"SVD 读取 {register.Path} @0x{register.Address:x8}: 0x{value:x}（宿主请求时间 {DateTimeOffset.UtcNow:O}）");
            return value;
        }
        finally { gate.Release(); }
    }
    public async Task WritePeripheralAsync(string deviceId, SvdRegister register, long revision, ulong value, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequirePeripheral(deviceId, register, revision);
            if (!register.CanWrite || register.Width < 64 && value >= (1UL << register.Width)) { throw Error("寄存器不可直接写入，或数值超过位宽。"); }
            // 只写用户确认的完整值，不先读再修改；W1C/W0C 寄存器不执行隐式读回。
            var bytes = new byte[register.Width / 8];
            for (var i = 0; i < bytes.Length; i++) { bytes[i] = (byte)(value >> (8 * i)); }
            if (!plotLittleEndian) { Array.Reverse(bytes); }
            await adapter!.SendAsync($"-data-write-memory-bytes 0x{register.Address:x8} {Convert.ToHexString(bytes)}", token);
            Trace($"SVD 写入 {register.Path} @0x{register.Address:x8}: 0x{value:x}；未自动读回。");
            PeripheralRevision++;
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }
    private void RequirePeripheral(string deviceId, SvdRegister register, long revision)
    {
        RequireStopped();
        if (!IsHardware || peripheralDeviceId != deviceId || PeripheralRevision != revision) { throw Error("外设读写需要匹配器件的实机暂停会话；目标状态已变化时请重新选择。"); }
        if (register.Width is not (8 or 16 or 32 or 64) || register.Address % (register.Width / 8) != 0 || (ulong)register.Address + (uint)(register.Width / 8) > (ulong)uint.MaxValue + 1)
        { throw Error("寄存器位宽、对齐或地址无效。"); }
    }
}
