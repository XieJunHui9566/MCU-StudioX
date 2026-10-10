namespace StudioX.Application.StcDebugging;

using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class Mon51DebugSession
{
    private CancellationTokenSource? stepLifetime;
    public bool IsSourceStepActive
    {
        get; private set;
    }
    public bool RequestStepPause()
    {
        if (!IsSourceStepActive)
        {
            return false;
        }
        stepLifetime?.Cancel();
        return true;
    }
    private Mcs51Instruction CurrentInstruction() => Mcs51Decoder.Decode(Pc, Convert.FromHexString(InstructionBytes));

    private async Task StepSourceAsync(bool over, CancellationToken token)
    {
        await CheckSourcesAsync(token);
        var instruction = CurrentInstruction();
        if (instruction.Opcode == 0xa5)
        {
            throw new StudioXException("MON51_INSTRUCTION", "当前 PC 已是 A5，无法确认为可执行原指令。");
        }
        if (over && instruction.IsCall)
        {
            await RunTemporaryAsync((ushort)(Pc + instruction.Length), "逐过程返回位置已到达", rawRegisters[15], token);
            return;
        }
        var initial = symbols?.LineAt(Pc);
        if (!SourceStepping || initial is null)
        {
            await StepCoreAsync(token);
            SetState(DebugState.Stopped, $"指令单步完成 · PC=0x{Pc:X4}");
            return;
        }
        stepLifetime?.Dispose();
        stepLifetime = new CancellationTokenSource();
        var firstPc = Pc;
        var started = DateTime.UtcNow;
        IsSourceStepActive = true;
        SetState(DebugState.Running, "源码单步中 · 可暂停");
        try
        {
            for (var i = 0; i < 128; i++)
            {
                // 取消在完整指令事务之间处理，避免截断串口响应而丢失目标状态。
                if (stepLifetime.IsCancellationRequested || DateTime.UtcNow - started > TimeSpan.FromSeconds(15))
                {
                    SetState(DebugState.Stopped, stepLifetime.IsCancellationRequested ? "源码单步已暂停" : "源码单步达到 15 秒上限，保持暂停；可切换指令步进");
                    return;
                }
                await StepCoreAsync(token);
                var next = symbols!.LineAt(Pc);
                if (Pc == firstPc || next is null || next.File != initial.File || next.Line != initial.Line || manualPoints.Contains(Pc) || sourcePoints.Any(p => p.Enabled && p.Verified && SourceAddress(p) == Pc))
                {
                    SetState(DebugState.Stopped, $"源码单步完成 · PC=0x{Pc:X4}");
                    return;
                }
                if (over && CurrentInstruction().IsCall)
                {
                    var call = CurrentInstruction();
                    await RunTemporaryAsync((ushort)(Pc + call.Length), "逐过程返回位置已到达", rawRegisters[15], token);
                    return;
                }
            }
            SetState(DebugState.Stopped, "源码单步达到 128 条指令上限，保持暂停；可切换指令步进");
        }
        finally { IsSourceStepActive = false; }
    }

    private async Task StepOutCoreAsync(CancellationToken token)
    {
        await CheckSourcesAsync(token);
        if (!HasSymbols)
        {
            throw new StudioXException("MON51_CAPABILITY", "跳出需要已核对的 CDB 和调用栈。");
        }
        if (verifiedReturn is not { } address)
        {
            throw new StudioXException("MON51_STACK", StackStatus + "；没有可验证的调用者返回地址，未启动跳出。");
        }
        await RunTemporaryAsync(address, "已跳出当前函数", returnStackPointer, token);
    }

    public async Task StepInstructionAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            selectedFrame = 0;
            await StepCoreAsync(token);
            SetState(DebugState.Stopped, $"指令单步完成 · PC=0x{Pc:X4}");
        }
        finally { gate.Release(); }
    }

    public async Task<DebugDisassembly> ReadDisassemblyAsync(uint? address = null, int count = 128, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            var start = address ?? Pc;
            if (start >= 0xdc00 || count is < 1 or > 512 || start + count > 0xdc00)
            {
                throw new StudioXException("MON51_RANGE", "反汇编须位于用户 CODE 0000–DBFF，单次 1–512 字节。");
            }
            var bytes = new byte[count];
            for (var i = 0; i < count; i += 128)
            {
                (await client!.ReadAsync(Mon51MemorySpace.Code, (ushort)(start + i), Math.Min(128, count - i), token)).CopyTo(bytes, i);
            }
            var rows = new List<DebugInstruction>();
            for (var offset = 0; offset < count;)
            {
                var size = Mcs51Decoder.Length(bytes[offset]);
                if (size > count - offset)
                {
                    break;
                }
                var instruction = Mcs51Decoder.Decode((ushort)(start + offset), bytes.AsSpan(offset));
                var function = symbols?.FunctionAt(instruction.Address);
                rows.Add(new(instruction.Address, Convert.ToHexString(instruction.Bytes), instruction.Text, function?.Name ?? "", function is null ? "" : (instruction.Address - function.Start).ToString()));
                offset += size;
            }
            return new(start, start + (uint)count, start, Pc, rows.ToArray());
        }
        finally { gate.Release(); }
    }
}
