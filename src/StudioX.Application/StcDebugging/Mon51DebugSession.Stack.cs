namespace StudioX.Application.StcDebugging;

using StudioX.Engine.Debugging;

public sealed partial class Mon51DebugSession
{
    private int returnStackPointer;

    private int? StackDelta(Mon51Function function, ushort pc)
    {
        var pending = new Queue<(ushort Address, int Delta)>();
        pending.Enqueue((function.Start, 0));
        var seen = new Dictionary<ushort, int>();
        int? result = null;
        while (pending.TryDequeue(out var item))
        {
            if (seen.Count > 4096 || Math.Abs(item.Delta) > 255)
            {
                return null;
            }
            if (seen.TryGetValue(item.Address, out var previous))
            {
                if (previous != item.Delta)
                {
                    return null;
                }
                continue;
            }
            seen[item.Address] = item.Delta;
            if (item.Address == pc)
            {
                result = item.Delta;
                continue;
            }
            if (!sourceInstructions.TryGetValue(item.Address, out var instruction) || item.Address < function.Start || item.Address > function.End)
            {
                continue;
            }
            var delta = item.Delta;
            if (instruction.Opcode == 0xc0)
            {
                delta++;
            }
            if (instruction.Opcode == 0xd0)
            {
                delta--;
            }
            if (instruction.Opcode == 0x05 && instruction.Bytes[1] == 0x81)
            {
                delta++;
            }
            if (instruction.Opcode == 0x15 && instruction.Bytes[1] == 0x81)
            {
                delta--;
            }
            // 写 SP 的其它指令不能用静态加减还原；不可把任意栈字节扫描为返回地址。
            if ((instruction.Opcode is 0x42 or 0x43 or 0x52 or 0x53 or 0x62 or 0x63 or 0x75 or 0xf5 or 0xc5 or 0xd5 or >= 0x86 and <= 0x8f) && instruction.Bytes[1] == 0x81 || instruction.Opcode == 0x85 && instruction.Bytes[2] == 0x81 || instruction.Opcode == 0xd0 && instruction.Bytes[1] == 0x81)
            {
                return null;
            }
            if (instruction.IsReturn || instruction.Opcode == 0x73)
            {
                continue;
            }
            if (!instruction.IsCall && instruction.Target is { } target && target >= function.Start && target <= function.End)
            {
                pending.Enqueue((target, delta));
            }
            if (!instruction.IsCall && (instruction.Opcode is 0x02 or 0x80 || (instruction.Opcode & 0x1f) == 1))
            {
                continue;
            }
            var next = instruction.Address + instruction.Length;
            if (next <= function.End)
            {
                pending.Enqueue(((ushort)next, delta));
            }
        }
        return result;
    }

    private async Task<DebugFrame[]> ReadFramesAsync(CancellationToken token)
    {
        verifiedReturn = null;
        var frames = new List<DebugFrame>();
        var function = symbols?.FunctionAt(Pc);
        var line = symbols?.LineAt(Pc);
        frames.Add(new(0, function?.Name ?? "<无源码映射>", line?.File ?? "", line?.Line ?? 0, $"0x{Pc:X4}"));
        if (symbols is null || function is null)
        {
            StackStatus = symbols is null ? "调用栈需要已核对的 CDB" : "当前 PC 没有完整函数范围；仅显示执行位置";
            return frames.ToArray();
        }
        var sp = (int)rawRegisters[15];
        var current = Pc;
        var stack = new byte[sp + 1];
        for (var start = 0; start < stack.Length; start += 128)
        {
            var data = await CachedReadAsync(Mon51MemorySpace.Idata, (ushort)start, Math.Min(128, stack.Length - start), token);
            data.CopyTo(stack, start);
        }
        while (frames.Count < 32 && function is not null)
        {
            var delta = StackDelta(function, current);
            if (delta is null)
            {
                StackStatus = "已核对 " + frames.Count + " 帧；当前函数的 SP 变化不可唯一还原";
                return frames.ToArray();
            }
            var high = sp - delta.Value;
            if (high < 1 || high >= stack.Length)
            {
                break;
            }
            var returnAddress = (ushort)(stack[high] << 8 | stack[high - 1]);
            var call = sourceInstructions.Values.FirstOrDefault(i => i.IsCall && i.Address + i.Length == returnAddress && i.Target == function.Start);
            if (call is null)
            {
                break;
            }
            var caller = symbols.FunctionAt(call.Address);
            if (caller is null || function.Interrupt)
            {
                break;
            }
            if (frames.Count == 1)
            {
                verifiedReturn = returnAddress;
                returnStackPointer = high - 2;
            }
            var location = symbols.LineAt(call.Address);
            frames.Add(new(frames.Count, caller.Name, location?.File ?? "", location?.Line ?? 0, $"0x{returnAddress:X4}"));
            sp = high - 2;
            current = returnAddress;
            function = caller;
        }
        StackStatus = $"已核对 {frames.Count} 帧 · 返回地址与 LCALL/ACALL 及 SP 变化一致；未验证的外层不显示";
        return frames.ToArray();
    }
}
