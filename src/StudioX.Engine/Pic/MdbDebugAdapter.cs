namespace StudioX.Engine.Pic;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

/// <summary>把 MDB 的真实响应转换为工作台快照；程序地址保持 PIC 程序字单位。</summary>
public sealed class MdbDebugAdapter : IDebugAdapter
{
    private sealed record Binding(SourceBreakpoint Point, string Remote, SourceLocation Location, string Address, int Hits = 0);
    private readonly MdbProcess process;
    private readonly string project, sourceRoot;
    private readonly int breakpointLimit;
    private readonly Dictionary<string, Binding> points = [];
    private readonly Dictionary<string, Binding> suspended = [];
    private readonly SemaphoreSlim stops = new(1, 1);
    private DebugFrame current = new(0, "", "", 0, "0x0");
    private DebugAction action;
    private bool suppressStops, disposed;
    private volatile bool pauseStepOut;
    private Task? stepOutTask;
    public event Action<string>? RecordReceived;
    public event Action<string>? Trace;
    public Func<Task>? FinishSession
    {
        get; set;
    }
    public uint FileRegisterEnd
    {
        get; init;
    }
    public uint ProgramWordEnd
    {
        get; init;
    }

    public MdbDebugAdapter(MdbProcess process, string project, string sourceRoot, int breakpointLimit)
    {
        this.process = process;
        this.project = project;
        this.sourceRoot = sourceRoot.Replace('\\', '/').TrimEnd('/');
        this.breakpointLimit = breakpointLimit;
        process.Output += OnOutput;
        process.Stopped += OnStop;
        process.Failed += OnFailure;
    }
    private void OnOutput(string text) => Trace?.Invoke(text);
    private void OnFailure(Exception failure) => RecordReceived?.Invoke("=studiox-transport-error,msg=" + MiRecord.Quote(failure.ToString()));
    private void OnStop(string text)
    {
        var location = ParseStop(text);
        if (location is null)
        {
            return;
        }
        current = location;
        if (!suppressStops && !disposed)
        {
            _ = PublishStopAsync(location);
        }
    }
    private async Task PublishStopAsync(DebugFrame frame)
    {
        await stops.WaitAsync();
        try
        {
            if (disposed)
            {
                return;
            }
            var table = await process.ExecuteAsync("info breakpoints");
            foreach (Match row in Regex.Matches(table, @"(?m)^\s*(\d+)\s+[yn]\s+(0x[0-9a-fA-F]+)\s+[^\r\n]*"))
            {
                var address = uint.Parse(row.Groups[2].Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (address == 0x7fffffff)
                {
                    continue;
                }
                var entry = points.FirstOrDefault(p => p.Value.Remote == row.Groups[1].Value);
                if (entry.Value is null)
                {
                    continue;
                }
                var location = address == uint.Parse(frame.Address.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    ? new SourceLocation(frame.File, frame.Line) : entry.Value.Location;
                points[entry.Key] = entry.Value with
                {
                    Address = "0x" + address.ToString("x", CultureInfo.InvariantCulture),
                    Location = location
                };
            }
            var pair = points.FirstOrDefault(p => p.Value.Point.Enabled &&
                (p.Value.Address.Equals(frame.Address, StringComparison.OrdinalIgnoreCase) || p.Value.Location.File == frame.File && p.Value.Location.Line == frame.Line));
            var binding = pair.Value;
            if (binding is not null)
            {
                binding = binding with
                {
                    Hits = binding.Hits + 1
                };
                points[pair.Key] = binding;
                var condition = true;
                if (binding.Point.Condition.Length > 0)
                {
                    condition = ParseNumber(await EvaluateAsync(binding.Point.Condition)) != 0;
                }
                if (action == DebugAction.Continue && !pauseStepOut && (binding.Hits <= binding.Point.IgnoreCount || !condition))
                {
                    await process.ExecuteAsync("continue");
                    return;
                }
                if (binding.Point.Temporary && condition && binding.Hits > binding.Point.IgnoreCount)
                {
                    await process.ExecuteAsync("delete " + binding.Remote);
                    points.Remove(pair.Key);
                    if (binding.Point.SessionOnly)
                    {
                        await RestoreSuspendedAsync(CancellationToken.None);
                    }
                }
            }
            EmitStop(frame, binding is null ? null : pair.Key, binding is null ? "end-stepping-range" : "breakpoint-hit");
        }
        catch (Exception failure) { OnFailure(failure); }
        finally { stops.Release(); }
    }
    private void EmitStop(DebugFrame frame, string? breakpoint, string reason) => RecordReceived?.Invoke(
        "*stopped,reason=" + MiRecord.Quote(reason) + (breakpoint is null ? "" : ",bkptno=" + MiRecord.Quote(breakpoint)) +
        ",frame={addr=" + MiRecord.Quote(frame.Address) + ",fullname=" + MiRecord.Quote(frame.File) + ",line=" + MiRecord.Quote(frame.Line.ToString(CultureInfo.InvariantCulture)) + "}");

    public async Task InitializeAsync(string compilerTarget, string elf, string? serial, CancellationToken token)
    {
        suppressStops = true;
        try
        {
            await process.ExecuteAsync("device PIC" + compilerTarget, token);
            foreach (var option in new[] { "poweroptions.powerenable false", "AutoSelectMemRanges auto", "memories.eeprom false", "memories.id false" })
            {
                await process.ExecuteAsync("set " + option, token);
            }
            if (serial is not null)
            {
                // 保留所有警告/错误；进程层仅处理已回读核对的准确器件提示，不使用全局 yes-to-all。
                await process.ExecuteAsync("set system.disableoutput false", token);
                // 程序镜像先由 IPECMD 保留 EEPROM/ID 地写入；MDB 仅在预留空间写入执行体，不再擦除。
                await process.ExecuteAsync("set programoptions.eraseb4program false", token);
            }
            await process.ExecuteAsync(serial is null ? "hwtool SIM" : "hwtool PICkit3 <sn>" + serial, token, TimeSpan.FromMinutes(2));
            var programmed = await process.ExecuteAsync("program " + Quote(elf), token, TimeSpan.FromMinutes(3));
            if (!programmed.Contains("Program succeeded.", StringComparison.Ordinal))
            {
                throw new StudioXException("PIC_DEBUG_PROGRAM", "MDB 未明确确认调试映像写入成功。\n" + programmed);
            }
            var entry = await process.ExecuteAsync("break main", token);
            var number = BreakpointNumber(entry);
            await process.ExecuteAsync("run", token);
            await process.ExecuteAsync("wait 10000", token, TimeSpan.FromSeconds(15));
            await process.ExecuteAsync("halt", token);
            await process.ExecuteAsync("delete " + number, token);
            var snapshot = await ReadAsync([], 0, token);
            if (snapshot.Frames.Length == 0 || snapshot.Frames.All(f => f.File.Length == 0 || f.Line < 1))
            {
                throw new StudioXException("PIC_DEBUG_SYMBOLS", "调试后端未返回当前 ELF 的源码位置，请核对调试构建和目标时钟。");
            }
        }
        finally { suppressStops = false; }
    }

    public async Task<(string Number, bool Verified, SourceLocation? Location)> InsertAsync(SourceBreakpoint point, CancellationToken token)
    {
        BreakpointOptions.From(point).Validate();
        if (!point.Enabled)
        {
            var disabled = "disabled-" + Guid.NewGuid().ToString("N");
            points[disabled] = new(point, disabled, new(point.File, point.Line), "");
            return (disabled, false, null);
        }
        if (points.Values.Count(p => p.Point.Enabled) >= breakpointLimit)
        {
            if (!point.SessionOnly)
            {
                throw new StudioXException("GDB_COMMAND", $"此型号只有 {breakpointLimit} 个硬件程序断点，请先禁用其他断点。");
            }
            // 光标临时借用有限的硬件槽，停止或删除光标断点后恢复原有断点的逻辑身份。
            foreach (var entry in points.Where(p => p.Value.Point.Enabled).ToArray())
            {
                await process.ExecuteAsync("delete " + entry.Value.Remote, token);
                suspended[entry.Key] = entry.Value;
                points.Remove(entry.Key);
            }
        }
        var file = sourceRoot + "/" + point.File.Replace('\\', '/');
        try
        {
            var response = await process.ExecuteAsync("break " + Quote(file + ":" + point.Line.ToString(CultureInfo.InvariantCulture)), token);
            var id = BreakpointNumber(response);
            var address = Regex.Match(response, @"(?i)(?:at|address:?)[ \t]+(0x[0-9a-f]+)").Groups[1].Value;
            var location = new SourceLocation(point.File, point.Line);
            points[id] = new(point, id, location, address);
            return (id, true, location);
        }
        catch
        {
            await RestoreSuspendedAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<MiValue> DeleteAsync(string id, CancellationToken token)
    {
        var cursor = points.TryGetValue(id, out var existing) && existing.Point.SessionOnly;
        if (points.TryGetValue(id, out var point) && point.Point.Enabled)
        {
            await process.ExecuteAsync("delete " + point.Remote, token);
        }
        points.Remove(id);
        if (cursor)
        {
            await RestoreSuspendedAsync(token);
        }
        return Empty;
    }

    private async Task RestoreSuspendedAsync(CancellationToken token)
    {
        foreach (var entry in suspended.ToArray())
        {
            suspended.Remove(entry.Key);
            var restored = await InsertAsync(entry.Value.Point, token);
            var binding = points[restored.Number];
            points.Remove(restored.Number);
            points[entry.Key] = binding with
            {
                Hits = entry.Value.Hits
            };
        }
    }
    public async Task<MiValue> EnableAsync(string id, bool enabled, CancellationToken token)
    {
        if (!points.TryGetValue(id, out var point) || point.Point.Enabled == enabled)
        {
            return Empty;
        }
        await DeleteAsync(id, token);
        var next = await InsertAsync(point.Point with
        {
            Enabled = enabled
        }, token);
        // 工作台保留逻辑 ID；适配器内保留实际 MDB 编号。
        var binding = points[next.Number];
        points.Remove(next.Number);
        points[id] = binding;
        return Empty;
    }

    public Task<MiValue[]> BreakpointInfoAsync(CancellationToken token = default) => Task.FromResult(points.Select(pair =>
        new MiValue(null, new Dictionary<string, MiValue>
        {
            ["number"] = MiValue.Scalar(pair.Key),
            ["times"] = MiValue.Scalar(pair.Value.Hits.ToString(CultureInfo.InvariantCulture)),
            ["ignore"] = MiValue.Scalar(Math.Max(0, pair.Value.Point.IgnoreCount - pair.Value.Hits).ToString(CultureInfo.InvariantCulture)),
            ["fullname"] = MiValue.Scalar(pair.Value.Location.File),
            ["line"] = MiValue.Scalar(pair.Value.Location.Line.ToString(CultureInfo.InvariantCulture))
        }.ToArray())).ToArray());

    public async Task<MiValue> ActionAsync(DebugAction next, CancellationToken token)
    {
        action = next;
        if (next == DebugAction.Pause && stepOutTask is { IsCompleted: false })
        {
            pauseStepOut = true;
            await stepOutTask.WaitAsync(token);
            return Empty;
        }
        pauseStepOut = next == DebugAction.Pause;
        if (next == DebugAction.StepOut)
        {
            var frames = ParseFrames(await process.ExecuteAsync("backtrace full", token));
            if (frames.Length < 2)
            {
                throw new StudioXException("GDB_COMMAND", "MDB 未返回可用调用者，不能跳出当前函数。");
            }
            suppressStops = true;
            stepOutTask = StepOutAsync(frames.Length);
            return Empty;
        }
        var response = await process.ExecuteAsync(next switch
        {
            DebugAction.Continue => "continue",
            DebugAction.Pause => "halt",
            DebugAction.StepInto => "step",
            DebugAction.StepOver => "next",
            DebugAction.Reset => "reset",
            _ => throw new ArgumentOutOfRangeException(nameof(next))
        }, token);
        // facade 在发命令前进入 Running；MDB 可能在 echo 屏障前已命中断点，不能再补发迟到的 running。
        if (next == DebugAction.Reset)
        {
            current = new(0, "", "", 0, "0x0");
        }
        else if (next == DebugAction.Pause && !response.Contains("Stop at", StringComparison.Ordinal))
        {
            EmitStop(current, null, "signal-received");
        }
        return Empty;
    }

    private async Task StepOutAsync(int depth)
    {
        try
        {
            for (var i = 0; i < 4096 && !pauseStepOut && !disposed; i++)
            {
                await process.ExecuteAsync("stepi");
                if (ParseFrames(await process.ExecuteAsync("backtrace full")).Length < depth)
                {
                    break;
                }
                if (i == 4095)
                {
                    Trace?.Invoke("跳出达到 4096 条指令上限，目标保持暂停，可继续单步或运行。");
                }
            }
            EmitStop(current, null, pauseStepOut ? "signal-received" : "end-stepping-range");
        }
        catch (Exception failure) { OnFailure(failure); }
        finally { suppressStops = false; }
    }

    public async Task<string> EvaluateAsync(string expression, CancellationToken token = default)
    {
        if (expression is "$pc" or "$PC")
        {
            return current.Address;
        }
        var parsed = DebugExpression.Parse(expression);
        if (parsed.Symbols.Count == 1 && expression.Trim() == parsed.Symbols[0])
        {
            var response = await process.ExecuteAsync("print /x " + parsed.Symbols[0].TrimStart('$'), token);
            var equal = response.IndexOf('=');
            if (equal < 0)
            {
                throw new StudioXException("MDB_VALUE", "MDB 未返回变量值：" + response);
            }
            var value = Regex.Replace(response[(equal + 1)..], @"(?:\r?\n)>\s*\z", "").Trim();
            return Regex.IsMatch(value, @"\A[0-9a-fA-F]+\z") ? "0x" + value : value;
        }
        var values = new Dictionary<string, long>();
        foreach (var symbol in parsed.Symbols.Distinct(StringComparer.Ordinal))
        {
            var value = await process.ExecuteAsync("print /x " + symbol.TrimStart('$'), token);
            var match = Regex.Match(value, @"=\s*(?:0x)?([0-9a-fA-F]+)\b");
            if (!match.Success)
            {
                throw new StudioXException("MDB_VALUE", "MDB 未返回可解析的整数值：" + value.Trim());
            }
            values[symbol] = long.Parse(match.Groups[1].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }
        return "0x" + parsed.Evaluate(name => values[name]).ToString("x", CultureInfo.InvariantCulture);
    }

    public async Task<DebugSnapshot> ReadAsync(IReadOnlyList<string> watches, int frame, CancellationToken token)
    {
        var stack = await process.ExecuteAsync("backtrace full", token);
        var frames = ParseFrames(stack);
        if (frames.Length == 0 && current.File.Length > 0)
        {
            frames = [current];
        }
        if (!frames.Any(f => f.Level == frame))
        {
            frame = 0;
        }
        var registers = new List<DebugRegister> { new("pc", current.Address) };
        foreach (var name in new[] { "WREG", "STATUS", "PCL", "PCLATH", "FSR", "INTCON" })
        {
            try
            {
                registers.Add(new(name, await EvaluateAsync(name, token)));
            }
            catch (StudioXException ex) when (ex.Code is "MDB_COMMAND" or "MDB_VALUE") { registers.Add(new(name, "不可用：" + ex.Message)); }
        }
        var locals = new List<DebugVariable>();
        var selected = -1;
        foreach (var line in stack.Split('\n'))
        {
            var header = Regex.Match(line.TrimStart('>', ' ', '\r'), @"^#(\d+)\s");
            if (header.Success)
            {
                selected = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else if (selected == frame && Regex.Match(line, @"^\s*([A-Za-z_]\w*)\s*=\s*(.+)$") is { Success: true } local)
            {
                locals.Add(new(local.Groups[1].Value, local.Groups[2].Value.Trim()));
            }
        }
        var watched = new List<DebugVariable>();
        foreach (var watch in watches)
        {
            if (frame != 0 && !registers.Any(r => r.Name == watch.TrimStart('$')))
            {
                // MDB 没有 frame 命令；仅使用指定栈帧的原始局部值，不能用顶层同名变量冒充。
                watched.Add(new(watch, locals.FirstOrDefault(v => v.Name == watch)?.Value ?? "不可用：MDB 不支持在调用者栈帧求值此表达式；可查看该帧的局部变量。"));
                continue;
            }
            try
            {
                watched.Add(new(watch, await EvaluateAsync(watch, token)));
            }
            catch (Exception ex) when (ex is StudioXException or ArgumentException) { watched.Add(new(watch, "不可用：" + ex.Message)); }
        }
        return new(registers.ToArray(), frames, locals.ToArray(), watched.ToArray(), frame);
    }

    public async Task<string> ReadMemoryAsync(uint address, int count, CancellationToken token)
    {
        if (count is < 1 or > 256 || (ulong)address + (uint)count > FileRegisterEnd)
        {
            throw new StudioXException("PIC_DEBUG_MEMORY", $"当前型号文件寄存器窗口结束地址为 0x{FileRegisterEnd:x}（不含），单次读取 1–256 字节；地址不是连续 RAM 容量。");
        }
        var response = await process.ExecuteAsync($"x /r{count}xb 0x{address:x}", token);
        var data = Regex.Replace(response, @"(?m)^\s*>?\s*", "").Trim();
        var bytes = Regex.Matches(data, @"\b[0-9A-Fa-f]{2}\b").Select(m => m.Value).ToArray();
        if (bytes.Length != count)
        {
            throw new StudioXException("MDB_MEMORY", "MDB 返回的文件寄存器数量与请求不符：" + response);
        }
        return string.Concat(bytes);
    }
    public async Task<DebugInstruction[]> ReadDisassemblyAsync(uint address, int byteCount, CancellationToken token = default)
    {
        if (byteCount is < 1 or > 512 || address + (ulong)byteCount > ProgramWordEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }
        var response = await process.ExecuteAsync($"x /p{byteCount}i 0x{address:x}", token);
        var rows = Regex.Matches(response, @"(?im)^\s*>?(0x[0-9a-f]+)\s+([^\r\n]+)").Select(m =>
            new DebugInstruction(uint.Parse(m.Groups[1].Value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture), "", m.Groups[2].Value.Trim(), "", "")).ToArray();
        if (rows.Length == 0)
        {
            throw new StudioXException("MDB_DISASSEMBLY", "MDB 未返回程序字反汇编。\n" + response);
        }
        var machineCode = await process.ExecuteAsync($"x /p{rows.Length}xh 0x{address:x}", token);
        var words = Regex.Matches(machineCode, @"\b[0-9a-fA-F]{4}\b").Select(m => m.Value.ToUpperInvariant()).ToArray();
        if (words.Length != rows.Length)
        {
            throw new StudioXException("MDB_DISASSEMBLY", "MDB 机器码和反汇编指令数量不一致。");
        }
        return rows.Select((row, index) => row with { Opcodes = words[index] }).ToArray();
    }
    public async Task WriteFileRegistersAsync(uint address, byte[] values, CancellationToken token = default)
    {
        if (values.Length is < 1 or > 64 || (ulong)address + (uint)values.Length > FileRegisterEnd)
        {
            throw new ArgumentOutOfRangeException(nameof(address));
        }
        await process.ExecuteAsync($"write /r 0x{address:x} " + string.Join(' ', values.Select(v => "0x" + v.ToString("x2", CultureInfo.InvariantCulture))), token);
        if (!string.Equals(await ReadMemoryAsync(address, values.Length, token), Convert.ToHexString(values), StringComparison.OrdinalIgnoreCase))
        {
            throw new StudioXException("PIC_DEBUG_WRITE", "文件寄存器写入后的回读不一致。");
        }
    }
    public Task<MiValue> SendAsync(string command, CancellationToken token = default) => command is "-stack-select-frame 0" or
        "-interpreter-exec console \"maintenance flush register-cache\"" or "-interpreter-exec console \"maintenance flush dcache\""
        ? Task.FromResult(Empty) : throw new StudioXException("PIC_DEBUG_CAPABILITY", "MDB 不提供此 GDB 专用操作。");
    private static MiValue Empty => new(null, []);
    private string Normalize(string file)
    {
        file = file.Trim().Replace('\\', '/');
        return file.StartsWith(sourceRoot + "/", StringComparison.OrdinalIgnoreCase) ? file[(sourceRoot.Length + 1)..]
            : Path.IsPathFullyQualified(file) && file.StartsWith(project.Replace('\\', '/') + "/", StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(project, file).Replace('\\', '/') : file;
    }
    private DebugFrame? ParseStop(string text)
    {
        var match = Regex.Match(text, @"address:\s*(0x[0-9a-fA-F]+)[\s>]+file:([^\r\n]+)[\s>]+source line:(\d+)");
        return match.Success ? new(0, "", Normalize(match.Groups[2].Value), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), match.Groups[1].Value) : null;
    }
    private DebugFrame[] ParseFrames(string text) => Regex.Matches(text, @"(?m)^\s*>?#(\d+)\s+(.+?)\s+\([^\r\n]*\)\s+at\s+(.+):(\d+)\s*$").Select(m =>
        new DebugFrame(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value, Normalize(m.Groups[3].Value), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
            m.Groups[1].Value == "0" ? current.Address : "")).ToArray();
    private static string BreakpointNumber(string text) => Regex.Match(text, @"Breakpoint\s+(\d+)\s+at", RegexOptions.IgnoreCase) is { Success: true } match
        ? match.Groups[1].Value : throw new StudioXException("MDB_BREAKPOINT", "MDB 未确认断点绑定：" + text);
    private static long ParseNumber(string text) => long.Parse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    private static string Quote(string value) => !value.Any(c => c is '"' or '\r' or '\n' or '\0') ? "\"" + value.Replace('\\', '/') + "\"" : throw new ArgumentException("Invalid MDB argument.");
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        pauseStepOut = true;
        process.Stopped -= OnStop;
        process.Failed -= OnFailure;
        if (stepOutTask is not null)
        {
            await stepOutTask;
        }
        try
        {
            if (process.IsAlive)
            {
                await process.ExecuteAsync("halt", timeout: TimeSpan.FromSeconds(10));
                await process.ExecuteAsync("delete");
                await process.ExecuteAsync("reset");
                await process.ExecuteAsync("run");
            }
        }
        finally
        {
            try
            {
                await process.DisposeAsync();
            }
            finally
            {
                process.Output -= OnOutput;
                if (FinishSession is not null)
                {
                    await FinishSession();
                }
            }
        }
    }
}
