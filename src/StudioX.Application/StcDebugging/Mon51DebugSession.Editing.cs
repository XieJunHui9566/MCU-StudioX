namespace StudioX.Application.StcDebugging;

using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class Mon51DebugSession
{
    public async Task WriteMemoryAsync(Mon51MemorySpace space, ushort address, byte[] bytes, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            await client!.WriteRamAsync(space, address, bytes, token);
            await ReadSnapshotAsync(token);
            SetState(DebugState.Stopped, $"{space} 0x{address:X4} 已写入 {bytes.Length} 字节并回读核对");
        }
        catch (StudioXException ex) when (ex.Code is "MON51_RANGE" or "MON51_WRITE_RANGE") { throw; }
        catch (Exception ex) { SetState(DebugState.Faulted, "内存修改未完整确认：" + ex.Message); Output?.Invoke(ex.ToString()); throw; }
        finally { gate.Release(); }
    }

    public async Task SetRegisterAsync(string name, uint value, CancellationToken token = default)
    {
        var normalized = name.Trim().TrimStart('$').ToUpperInvariant();
        if (normalized == "PC")
        {
            if (value > ushort.MaxValue)
            {
                throw new StudioXException("MON51_REGISTER", "PC 超过 16 位。");
            }
            await SetPcAsync((ushort)value, token);
            return;
        }
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            if (normalized is "CY" or "AC" or "OV" or "寄存器组")
            {
                var maximum = normalized == "寄存器组" ? 3u : 1u;
                if (value > maximum)
                {
                    throw new StudioXException("MON51_REGISTER", "标志或寄存器组值越界。");
                }
                var shift = normalized switch
                {
                    "CY" => 7,
                    "AC" => 6,
                    "OV" => 2,
                    _ => 3
                };
                value = (uint)((rawRegisters[14] & ~(maximum << shift)) | value << shift);
                normalized = "PSW";
            }
            if (value > (normalized == "DPTR" ? 0xffffu : 0xffu))
            {
                throw new StudioXException("MON51_REGISTER", "寄存器值超出位宽。");
            }
            if (normalized == "DPTR")
            {
                await client!.WriteContextAsync(0x82, (byte)value, token);
                await client.WriteContextAsync(0x83, (byte)(value >> 8), token);
            }
            else
            {
                var address = normalized switch
                {
                    "A" => 0xe0,
                    "B" => 0xf0,
                    "SP" => 0x81,
                    "PSW" => 0xd0,
                    _ when normalized.Length == 2 && normalized[0] == 'R' && normalized[1] is >= '0' and <= '7' => (rawRegisters[14] & 0x18) + normalized[1] - '0',
                    _ => throw new StudioXException("MON51_REGISTER", "此寄存器只读或不存在；P 是由 A 自动计算的奇偶标志。")
                };
                await client!.WriteContextAsync((ushort)address, (byte)value, token);
            }
            await ReadSnapshotAsync(token);
            var expected = normalized == "PSW" ? value & 0xfe : value;
            var actual = normalized == "PSW" ? RegisterValue(normalized) & 0xfe : RegisterValue(normalized);
            if (actual != expected)
            {
                throw new StudioXException("MON51_VERIFY", "寄存器修改回读不一致。");
            }
            SetState(DebugState.Stopped, $"{normalized} 已修改并核对 · PC=0x{Pc:X4}");
        }
        catch (StudioXException ex) when (ex.Code == "MON51_REGISTER") { throw; }
        catch (Exception ex) { SetState(DebugState.Faulted, "寄存器修改未完整确认：" + ex.Message); Output?.Invoke(ex.ToString()); throw; }
        finally { gate.Release(); }
    }

    public async Task SetVariableAsync(string expression, long value, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            await CheckSourcesAsync(token);
            var symbol = await ResolveVariableAsync(expression, token);
            if (symbol.OnStack || symbol.Address is null || symbol.Size is < 1 or > 4 || symbol.Space is not ('E' or 'F' or 'G') || symbol.TypeChain.StartsWith('D') || symbol.TypeChain.StartsWith("ST", StringComparison.Ordinal) || symbol.TypeChain.StartsWith("SF", StringComparison.Ordinal))
            {
                throw new StudioXException("MON51_VARIABLE", "变量编辑仅开放有固定 RAM 地址的 1–4 字节整数；其它位置可通过明确的寄存器／内存操作修改。");
            }
            var bits = symbol.Size * 8;
            var min = symbol.IsSigned ? -(1L << (bits - 1)) : 0;
            var max = symbol.IsSigned ? (1L << (bits - 1)) - 1 : (1L << bits) - 1;
            if (value < min || value > max)
            {
                throw new StudioXException("MON51_VARIABLE", "写入值超出变量类型范围。");
            }
            var space = symbol.Space switch
            {
                'F' => Mon51MemorySpace.Xdata,
                'G' => Mon51MemorySpace.Idata,
                _ => Mon51MemorySpace.DataSfr
            };
            await client!.WriteRamAsync(space, symbol.Address.Value, Enumerable.Range(0, symbol.Size).Select(i => (byte)(value >> (i * 8))).ToArray(), token);
            await ReadSnapshotAsync(token);
            SetState(DebugState.Stopped, expression + " 已修改并回读核对");
        }
        catch (StudioXException ex) when (ex.Code is "MON51_VARIABLE" or "MON51_EXPRESSION" or "MON51_SOURCE_CHANGED" or "MON51_WRITE_RANGE" or "MON51_RANGE") { throw; }
        catch (Exception ex) { SetState(DebugState.Faulted, "变量修改未完整确认：" + ex.Message); Output?.Invoke(ex.ToString()); throw; }
        finally { gate.Release(); }
    }
}
