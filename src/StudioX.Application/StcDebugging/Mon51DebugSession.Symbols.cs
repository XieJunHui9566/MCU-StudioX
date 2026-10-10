namespace StudioX.Application.StcDebugging;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class Mon51DebugSession
{
    private Mon51Symbols? symbols;
    private StcDebugArtifact? artifact;
    private byte[] rawRegisters = [];
    private SourceBreakpoint[] sourcePoints = [];
    private string[] watchExpressions = [];
    private readonly HashSet<ushort> manualPoints = [];
    private readonly Dictionary<ushort, Mcs51Instruction> sourceInstructions = [];
    private readonly Dictionary<string, byte[]> memoryCache = [];
    private readonly Dictionary<string, string> sourceTexts = new(StringComparer.OrdinalIgnoreCase);
    private int selectedFrame;
    private ushort? verifiedReturn;
    public bool HasSymbols => symbols is not null;
    public bool SourceStepping { get; set; } = true;
    public string SymbolsStatus { get; private set; } = "未加载源码符号 · 可使用地址调试";
    public string StackStatus { get; private set; } = "暂停后读取调用栈";
    public IReadOnlyList<SourceBreakpoint> SourceBreakpoints => sourcePoints;
    public IReadOnlyList<string> Watches => watchExpressions;
    public Func<Task>? PreferencesChanged
    {
        get; set;
    }

    public async Task LoadSymbolsAsync(StcDebugArtifact bundle, CancellationToken token = default)
    {
        var parsed = Mon51Symbols.Parse(bundle.SymbolsText, bundle.ProjectDirectory, bundle.SourceFiles);
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in parsed.Lines.Select(l => l.File).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _ = PathBoundary.Resolve(bundle.ProjectDirectory, Path.GetRelativePath(bundle.ProjectDirectory, file).Replace('\\', '/'));
            var text = await File.ReadAllTextAsync(file, token);
            var count = text.Split('\n').Length;
            if (parsed.Lines.Any(l => l.File == file && l.Line > count))
            {
                throw new StudioXException("MON51_CDB", "CDB 源码行越过文件末尾：" + file);
            }
            texts[file] = text;
        }
        var decoded = new Dictionary<ushort, Mcs51Instruction>();
        foreach (var function in parsed.Functions)
        {
            for (var address = (int)function.Start; address <= function.End;)
            {
                var size = Mcs51Decoder.Length(bundle.Code[address]);
                if (address + size > bundle.Code.Length || Enumerable.Range(address, size).Any(i => !bundle.Present[i]))
                {
                    throw new StudioXException("MON51_CDB", "CDB 函数范围没有完整 HEX 指令：" + function.Name);
                }
                decoded[(ushort)address] = Mcs51Decoder.Decode((ushort)address, bundle.Code.AsSpan(address, size));
                address += size;
            }
        }
        if (parsed.Lines.Any(l => !decoded.ContainsKey(l.Address)))
        {
            throw new StudioXException("MON51_CDB", "CDB 源码行未指向完整指令边界。");
        }
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            // 对 HEX 中的每段数据逐字节回读，不以 PC 命中或 CDB 文件存在冒充固件匹配。
            for (var address = 0; address < bundle.Present.Length;)
            {
                if (!bundle.Present[address])
                {
                    address++;
                    continue;
                }
                var start = address;
                while (address < bundle.Present.Length && bundle.Present[address] && address - start < 128)
                {
                    address++;
                }
                var actual = await client!.ReadAsync(Mon51MemorySpace.Code, (ushort)start, address - start, token);
                if (!actual.SequenceEqual(bundle.Code.AsSpan(start, actual.Length).ToArray()))
                {
                    // 核对失败后撤销旧映射；不能继续沿用旧会话中已核对的源码标记。
                    artifact = null;
                    symbols = null;
                    sourceInstructions.Clear();
                    sourceTexts.Clear();
                    SymbolsStatus = "目标固件不匹配 · 当前为地址调试";
                    await RebindSourcePointsAsync(token);
                    await ReadSnapshotAsync(token);
                    SetState(DebugState.Stopped, SymbolsStatus);
                    throw new StudioXException("MON51_IMAGE_MISMATCH", $"目标 CODE 0x{start:X4} 与所选构建不同；未启用源码调试。请下载明确选定的对应固件，或继续使用地址调试。");
                }
            }
            artifact = bundle;
            symbols = parsed;
            sourceInstructions.Clear();
            foreach (var pair in decoded)
            {
                sourceInstructions[pair.Key] = pair.Value;
            }
            sourceTexts.Clear();
            foreach (var pair in texts)
            {
                sourceTexts[pair.Key] = pair.Value;
            }
            SymbolsStatus = $"源码调试已核对 · {parsed.Functions.Count} 个函数 / {parsed.Lines.Count} 个行映射 · HEX 与目标 CODE 一致";
            await RebindSourcePointsAsync(token);
            await ReadSnapshotAsync(token);
            SetState(DebugState.Stopped, SymbolsStatus + $" · PC=0x{Pc:X4}");
            Output?.Invoke($"HEX: {bundle.ImagePath} · SHA-256 {bundle.ImageSha256}\nCDB: {bundle.SymbolsPath} · SHA-256 {bundle.SymbolsSha256}\n源码指纹: {bundle.SourceStamp}");
        }
        catch (StudioXException ex) when (ex.Code is "MON51_IMAGE_MISMATCH" or "MON51_CDB" or "MON51_STATE" or "MON51_BREAKPOINT_RANGE" or "MON51_BREAKPOINT_ORIGINAL") { throw; }
        catch (Exception ex)
        {
            SetState(DebugState.Faulted, "符号核对未完整确认，请结束会话恢复断点：" + ex.Message);
            Output?.Invoke(ex.ToString());
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task SetPreferencesAsync(IEnumerable<SourceBreakpoint> pointsToSet, IEnumerable<string> watchesToSet, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            sourcePoints = pointsToSet.Where(p => !p.SessionOnly).Select(p => p with { Verified = false, BoundLocation = null, HitCount = 0, IgnoreRemaining = p.IgnoreCount }).ToArray();
            watchExpressions = watchesToSet.ToArray();
            await RebindSourcePointsAsync(token);
            await ReadSnapshotAsync(token);
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    public async Task ReportSymbolsUnavailableAsync(string reason, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            await RestoreAsync(token);
            artifact = null;
            symbols = null;
            sourceInstructions.Clear();
            sourceTexts.Clear();
            SymbolsStatus = "源码符号未启用：" + reason + " · 可使用地址调试";
            await RebindSourcePointsAsync(token);
            await ReadSnapshotAsync(token);
            SetState(DebugState.Stopped, SymbolsStatus);
        }
        finally { gate.Release(); }
    }

    public async Task SelectFrameAsync(int? frame, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            if (frame is { } level && (level < 0 || level >= Snapshot.Frames.Length))
            {
                throw new StudioXException("MON51_STACK", "所选栈帧已不可用。");
            }
            selectedFrame = frame ?? selectedFrame;
            await ReadSnapshotAsync(token);
            Changed?.Invoke();
        }
        finally { gate.Release(); }
    }

    private async Task CheckSourcesAsync(CancellationToken token)
    {
        foreach (var pair in sourceTexts)
        {
            if (!File.Exists(pair.Key) || await File.ReadAllTextAsync(pair.Key, token) != pair.Value)
            {
                throw new StudioXException("MON51_SOURCE_CHANGED", "已加载的源码在会话中改变；请结束调试、重新编译和核对固件：" + pair.Key);
            }
        }
    }

    private long RegisterValue(string name)
    {
        var value = Snapshot.Registers.FirstOrDefault(r => r.Name.Equals(name.TrimStart('$'), StringComparison.OrdinalIgnoreCase))?.Value;
        return value is null ? throw new StudioXException("MON51_EXPRESSION", "未知 8051 寄存器：" + name) : value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? long.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : long.Parse(value, CultureInfo.InvariantCulture);
    }

    private async Task<byte[]> CachedReadAsync(Mon51MemorySpace space, ushort address, int count, CancellationToken token)
    {
        var key = $"{space}:{address}:{count}";
        if (!memoryCache.TryGetValue(key, out var data))
        {
            data = await client!.ReadAsync(space, address, count, token);
            memoryCache[key] = data;
        }
        return data;
    }

    private async Task<byte[]> ReadSymbolAsync(Mon51Symbol symbol, CancellationToken token)
    {
        if (symbol.Size is < 1 or > 128 || symbol.IsFunction)
        {
            throw new StudioXException("MON51_VARIABLE", "该类型不可作为单次标量读取。");
        }
        if (symbol.Space == 'R')
        {
            if (selectedFrame != 0 || symbol.Registers.Length != symbol.Size)
            {
                throw new StudioXException("MON51_VARIABLE", "调用者寄存器值或优化后的寄存器位置不可还原。");
            }
            return symbol.Registers.Select(r => checked((byte)RegisterValue(r))).ToArray();
        }
        var address = symbol.Address;
        if (symbol.OnStack)
        {
            if (selectedFrame != 0 || symbol.Space != 'B')
            {
                throw new StudioXException("MON51_VARIABLE", "仅当前帧的 SDCC 内部栈局部变量可可靠定位。");
            }
            var function = symbols!.FunctionAt(Pc);
            var delta = function is null ? null : StackDelta(function, Pc);
            if (delta is null || delta < Math.Max(1, symbol.StackOffset + 1))
            {
                throw new StudioXException("MON51_VARIABLE", "当前函数尚未建立或已释放相应的内部栈位置。");
            }
            var bp = symbols!.Symbols.FirstOrDefault(s => s.Name == "bp" && s.Address is not null && !s.OnStack);
            byte basis;
            if (bp is not null)
            {
                basis = (await ReadSymbolAsync(bp, token))[0];
            }
            else
            {
                if (function is null || !sourceInstructions.TryGetValue(function.Start, out var push) || push.Opcode != 0xc0 || push.Bytes[1] >= 128 ||
                    !sourceInstructions.TryGetValue((ushort)(push.Address + push.Length), out var move) || move.Opcode != 0x85 || move.Bytes[1] != 0x81 || move.Bytes[2] != push.Bytes[1] || Pc < move.Address + move.Length)
                {
                    throw new StudioXException("MON51_VARIABLE", "缺少 bp 符号，且尚未执行可验证的 SDCC 保存 bp / 设置 bp=SP 序言。");
                }
                basis = (await CachedReadAsync(Mon51MemorySpace.Idata, push.Bytes[1], 1, token))[0];
            }
            var absolute = basis + symbol.StackOffset;
            if (absolute < 0 || absolute + symbol.Size > 256 || absolute + symbol.Size - 1 > rawRegisters[15])
            {
                throw new StudioXException("MON51_VARIABLE", "栈局部变量超出已分配范围。");
            }
            address = (ushort)absolute;
        }
        if (address is null)
        {
            throw new StudioXException("MON51_VARIABLE", "符号无链接位置，可能已被优化。");
        }
        if (symbol.Space is 'H' or 'J')
        {
            var bitAddress = address.Value;
            if (bitAddress > 255)
            {
                throw new StudioXException("MON51_VARIABLE", "位地址越界。");
            }
            var byteAddress = symbol.Space == 'H' ? (ushort)(0x20 + bitAddress / 8) : (ushort)(bitAddress & 0xf8);
            var data = await ReadSymbolAsync(symbol with
            {
                Size = 1,
                Space = symbol.Space == 'H' ? 'E' : 'I',
                Address = byteAddress
            }, token);
            return [(byte)((data[0] >> (bitAddress & 7)) & 1)];
        }
        if (symbol.Space == 'I')
        {
            // A/B/DPTR/PSW/SP 使用暂停寄存器上下文，避免显示监控程序执行期间的物理 SFR 值。
            var special = address.Value switch
            {
                0xe0 => "A",
                0xf0 => "B",
                0xd0 => "PSW",
                0x81 => "SP",
                _ => null
            };
            if (special is not null && symbol.Size == 1)
            {
                return [(byte)RegisterValue(special)];
            }
            if (address == 0x82 && symbol.Size is 1 or 2)
            {
                return symbol.Size == 1 ? [(byte)RegisterValue("DPTR")] : [(byte)RegisterValue("DPTR"), (byte)(RegisterValue("DPTR") >> 8)];
            }
            if (address == 0x83 && symbol.Size == 1)
            {
                return [(byte)(RegisterValue("DPTR") >> 8)];
            }
            throw new StudioXException("MON51_VARIABLE", "其它 SFR 须在 8051 内存窗口中明确选择读取；读取可能产生外设副作用。");
        }
        var space = symbol.Space switch
        {
            'E' => Mon51MemorySpace.DataSfr,
            'G' or 'B' => Mon51MemorySpace.Idata,
            'F' => Mon51MemorySpace.Xdata,
            'C' or 'D' => Mon51MemorySpace.Code,
            _ => throw new StudioXException("MON51_VARIABLE", "暂不能可靠定位的 SDCC 存储类型：" + symbol.Space)
        };
        if (symbol.Space == 'E' && address.Value + symbol.Size > 128)
        {
            throw new StudioXException("MON51_VARIABLE", "DATA 变量越过下 128 字节。");
        }
        return await CachedReadAsync(space, address.Value, symbol.Size, token);
    }

    private Mon51Symbol FindSymbol(string name)
    {
        if (symbols is null)
        {
            throw new StudioXException("MON51_CAPABILITY", "变量观察需要已核对的 CDB 和目标固件。");
        }
        var frame = Snapshot.Frames.FirstOrDefault(f => f.Level == selectedFrame);
        var location = frame is null ? null : symbols.LineAt(ushort.Parse(frame.Address[2..], NumberStyles.HexNumber));
        var functionLocals = symbols.Symbols.Where(s => !s.IsFunction && s.Name == name && LocalScope(s, frame)).ToArray();
        var locals = functionLocals.Where(s => s.Block == 0 || location is not null && s.Block == location.Block).ToArray();
        if (locals.Length == 0 && functionLocals.Length == 1 && functionLocals[0].Space == 'R')
        {
            locals = functionLocals;
        }
        var candidates = locals.Length > 0 ? locals : symbols.Symbols.Where(s => !s.IsFunction && s.Name == name && (s.Scope == "G" || s.Scope.StartsWith('F') && frame is not null && Path.GetFileNameWithoutExtension(frame.File) == s.Scope[1..])).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new StudioXException("MON51_VARIABLE", candidates.Length == 0 ? "当前作用域没有符号：" + name : "同名符号作用域不明确：" + name);
    }
    private static bool LocalScope(Mon51Symbol symbol, DebugFrame? frame) => frame is not null &&
        (symbol.Scope == "L" + frame.Function || symbol.Scope == "L" + Path.GetFileNameWithoutExtension(frame.File) + "." + frame.Function);

    private static long Integer(byte[] data, bool signed)
    {
        if (data.Length is < 1 or > 8)
        {
            throw new StudioXException("MON51_VARIABLE", "聚合类型不能用于整数表达式。");
        }
        ulong value = 0;
        for (var i = 0; i < data.Length; i++)
        {
            value |= (ulong)data[i] << (8 * i);
        }
        if (signed && data.Length < 8 && (data[^1] & 0x80) != 0)
        {
            value |= ulong.MaxValue << (data.Length * 8);
        }
        return unchecked((long)value);
    }

    private async Task<Mon51Symbol> ResolveVariableAsync(string expression, CancellationToken token)
    {
        var match = Regex.Match(expression, @"^([A-Za-z_]\w*)((?:(?:\.|->)[A-Za-z_]\w*|\[\d+\])*)$");
        if (!match.Success)
        {
            throw new StudioXException("MON51_EXPRESSION", "请输入变量、成员、固定数组下标或只读整数表达式。");
        }
        var symbol = FindSymbol(match.Groups[1].Value);
        foreach (Match part in Regex.Matches(match.Groups[2].Value, @"(\.|->)([A-Za-z_]\w*)|\[(\d+)\]"))
        {
            if (part.Groups[3].Success)
            {
                var array = Regex.Match(symbol.TypeChain, @"^DA(\d+)d?,(.+)$");
                var index = int.Parse(part.Groups[3].Value);
                if (array.Success)
                {
                    if (!int.TryParse(array.Groups[1].Value, out var count) || count < 1 || index >= count || symbol.Size % count != 0 || symbol.Address is null || symbol.OnStack || symbol.Space == 'R')
                    {
                        throw new StudioXException("MON51_VARIABLE", "数组下标越界或数组位置不可可靠展开。");
                    }
                    var elementSize = symbol.Size / count;
                    symbol = symbol with
                    {
                        Size = elementSize,
                        TypeChain = array.Groups[2].Value,
                        Address = checked((ushort)(symbol.Address.Value + index * elementSize))
                    };
                }
                else
                {
                    symbol = await DereferenceAsync(symbol, token);
                    symbol = symbol with
                    {
                        Address = checked((ushort)(symbol.Address!.Value + index * symbol.Size))
                    };
                }
            }
            else
            {
                if (part.Groups[1].Value == "->")
                {
                    symbol = await DereferenceAsync(symbol, token);
                }
                var structure = Regex.Match(symbol.TypeChain, @"^ST([^:]+):[SU]$");
                if (!structure.Success || !symbols!.Structures.TryGetValue(symbol.Module + "." + structure.Groups[1].Value, out var members) || symbol.Address is null || symbol.OnStack || symbol.Space == 'R')
                {
                    throw new StudioXException("MON51_VARIABLE", "结构类型或存储位置不可可靠展开。");
                }
                var member = members.SingleOrDefault(m => m.Name == part.Groups[2].Value) ?? throw new StudioXException("MON51_VARIABLE", "结构成员不存在。");
                symbol = member with
                {
                    Space = symbol.Space,
                    Address = checked((ushort)(symbol.Address.Value + member.Address!.Value))
                };
            }
        }
        return symbol;
    }

    private async Task<Mon51Symbol> DereferenceAsync(Mon51Symbol symbol, CancellationToken token)
    {
        var pointer = await ReadSymbolAsync(symbol, token);
        var storage = symbol.TypeChain.Split(',')[0] switch
        {
            "DX" => 'F',
            "DD" => 'E',
            "DI" => 'G',
            "DC" => 'C',
            "DG" when pointer.Length == 3 => pointer[2] switch { 0 => 'F', 0x40 => 'G', 0x80 => 'C', _ => '?' },
            _ => '?'
        };
        if (storage == '?' || pointer.Length is < 1 or > 3)
        {
            throw new StudioXException("MON51_VARIABLE", "指针存储类型尚不能可靠解析。");
        }
        var chain = symbol.TypeChain[(symbol.TypeChain.IndexOf(',') + 1)..];
        var size = chain.Split(':')[0] switch
        {
            "SC" or "SX" => 1,
            "SI" or "SS" => 2,
            "SL" or "SF" => 4,
            _ => 0
        };
        if (chain.StartsWith("ST", StringComparison.Ordinal) && symbols!.Structures.TryGetValue(symbol.Module + "." + chain[2..chain.IndexOf(':')], out var members))
        {
            size = members.Max(m => (m.Address ?? 0) + m.Size);
        }
        if (size < 1)
        {
            throw new StudioXException("MON51_VARIABLE", "指针的目标类型大小不可可靠解析。");
        }
        return symbol with
        {
            Space = storage,
            Address = (ushort)(pointer[0] | (pointer.Length > 1 ? pointer[1] << 8 : 0)),
            TypeChain = chain,
            Size = size,
            OnStack = false,
            Registers = []
        };
    }

    private async Task<long> EvaluateCoreAsync(string expression, CancellationToken token)
    {
        if (expression.StartsWith('$') && Regex.IsMatch(expression, @"^\$\w+$"))
        {
            return RegisterValue(expression);
        }
        if (Regex.IsMatch(expression, @"^[A-Za-z_]\w*(?:(?:\.|->)[A-Za-z_]\w*|\[\d+\])*$"))
        {
            var variable = await ResolveVariableAsync(expression, token);
            if (variable.TypeChain.StartsWith("SF:", StringComparison.Ordinal))
            {
                throw new StudioXException("MON51_EXPRESSION", "浮点变量可观察，但不参与整数条件计算。");
            }
            return Integer(await ReadSymbolAsync(variable, token), variable.IsSigned);
        }
        var parsed = DebugExpression.Parse(expression);
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in parsed.Symbols)
        {
            values[name] = await EvaluateCoreAsync(name, token);
        }
        return parsed.Evaluate(name => values[name]);
    }

    public async Task<string> EvaluateAsync(string expression, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            RequireStopped();
            memoryCache.Clear();
            return (await EvaluateCoreAsync(expression, token)).ToString(CultureInfo.InvariantCulture);
        }
        finally { gate.Release(); }
    }

    private async Task<DebugVariable> VariableRowAsync(string name, DebugSnapshot previous, CancellationToken token)
    {
        try
        {
            string value;
            string type;
            if (name.StartsWith('$'))
            {
                var integer = RegisterValue(name);
                value = $"{integer} (0x{integer:X})";
                type = "8051 寄存器";
            }
            else if (Regex.IsMatch(name, @"^[A-Za-z_]\w*(?:(?:\.|->)[A-Za-z_]\w*|\[\d+\])*$"))
            {
                var symbol = await ResolveVariableAsync(name, token);
                var data = await ReadSymbolAsync(symbol, token);
                type = symbol.TypeName + (symbol.Space == 'R' ? " · CDB 寄存器位置，优化可能复用" : "");
                value = symbol.TypeChain.StartsWith("SF:", StringComparison.Ordinal) && data.Length == 4 ? BitConverter.ToSingle(data).ToString("R", CultureInfo.InvariantCulture)
                    : symbol.TypeChain.StartsWith("DA", StringComparison.Ordinal) || symbol.TypeChain.StartsWith("ST", StringComparison.Ordinal) || symbol.TypeChain.StartsWith('D') ? "{" + string.Join(' ', data.Select(b => b.ToString("X2"))) + "}"
                    : $"{Integer(data, symbol.IsSigned)} (0x{Convert.ToHexString(data.Reverse().ToArray())})";
            }
            else
            {
                value = (await EvaluateCoreAsync(name, token)).ToString(CultureInfo.InvariantCulture);
                type = "只读整数表达式";
            }
            return new(name, value, type, previous.Watches.Concat(previous.Locals).Any(v => v.Name == name && v.Value != value));
        }
        catch (Exception ex) when (ex is StudioXException { Code: "MON51_VARIABLE" or "MON51_EXPRESSION" or "MON51_CAPABILITY" or "MON51_RANGE" } or InvalidOperationException or ArgumentException or OverflowException)
        {
            return new(name, "不可用：" + ex.Message);
        }
    }

    private async Task ReadSourceSnapshotAsync(DebugSnapshot? comparison, CancellationToken token)
    {
        memoryCache.Clear();
        var previous = comparison ?? previousSourceSnapshot;
        var frames = await ReadFramesAsync(token);
        selectedFrame = Math.Clamp(selectedFrame, 0, Math.Max(0, frames.Length - 1));
        Snapshot = Snapshot with
        {
            Frames = frames,
            SelectedFrame = selectedFrame
        };
        var names = symbols is null ? [] : symbols.Symbols.Where(s => !s.IsFunction && LocalScope(s, frames[selectedFrame])).Select(s => s.Name).Distinct().Take(64).ToArray();
        var locals = new List<DebugVariable>();
        foreach (var name in names)
        {
            locals.Add(await VariableRowAsync(name, previous, token));
        }
        var watches = new List<DebugVariable>();
        foreach (var name in watchExpressions)
        {
            watches.Add(await VariableRowAsync(name, previous, token));
        }
        Snapshot = Snapshot with
        {
            Locals = locals.ToArray(),
            Watches = watches.ToArray()
        };
        previousSourceSnapshot = Snapshot;
    }
    private DebugSnapshot previousSourceSnapshot = DebugSnapshot.Empty;
}
