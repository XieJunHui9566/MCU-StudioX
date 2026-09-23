namespace StudioX.Engine.Debugging;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>只在内存里响应 MI，不创建进程/socket，不调用 OpenOCD。确定性模型只对应 F407DebugExample。</summary>
public sealed class SimulatedF407Transport : IGdbMiTransport
{
    private sealed record Breakpoint(string Id, string File, int Line, bool Enabled, bool Verified,
        string Condition, int IgnoreRemaining, bool Temporary, int HitCount = 0);
    private readonly object sync = new();
    private readonly Dictionary<string, Breakpoint> breakpoints = [];
    private readonly CancellationTokenSource lifetime = new();
    private Task runner = Task.CompletedTask;
    private int generation, nextBreakpoint, frame, line = F407DebugExample.Entry;
    private uint counter, input, output, doubled;
    private bool running, disposed;
    public event Action<string>? RecordReceived;
    public async Task<string> ExecuteAsync(string command, CancellationToken token = default)
    {
        await Task.Delay(8, token);
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var split = command.IndexOf('-');
            var id = command[..split]; var text = command[split..];
            try { return id + Execute(text); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or OverflowException) { return id + "^error,msg=" + Q(ex.Message); }
        }
    }
    private string Execute(string text)
    {
        if (text.StartsWith("-break-insert ", StringComparison.Ordinal))
        {
            var arguments = Arguments(text);
            var location = arguments[^1];
            var colon = location.LastIndexOf(':');
            var file = location[..colon].Replace('\\', '/'); var number = int.Parse(location[(colon + 1)..], CultureInfo.InvariantCulture);
            // main 的左花括号无指令；模拟 GDB 把该行断点解析到下一条可执行语句。
            var resolved = number == F407DebugExample.Entry - 1 ? F407DebugExample.Entry : number;
            var verified = file == F407DebugExample.RelativeFile && F407DebugExample.ExecutableLines.Contains(resolved);
            var enabled = !arguments.Contains("-d"); var temporary = arguments.Contains("-t");
            var conditionIndex = arguments.IndexOf("-c"); var ignoreIndex = arguments.IndexOf("-i");
            var condition = conditionIndex >= 0 ? arguments[conditionIndex + 1] : "";
            var ignore = ignoreIndex >= 0 ? int.Parse(arguments[ignoreIndex + 1], CultureInfo.InvariantCulture) : 0;
            if (condition.Length > 0)
                foreach (var symbol in DebugExpression.Parse(condition).Symbols) _ = Lookup(symbol, number, 0);
            if (enabled && verified && breakpoints.Values.Count(b => b.Enabled && b.Verified) >= 6) throw new InvalidOperationException("离线模型的 6 个硬件断点已用完，请禁用或删除一个断点。");
            var bp = new Breakpoint((++nextBreakpoint).ToString(CultureInfo.InvariantCulture), file, resolved, enabled, verified, condition, ignore, temporary); breakpoints.Add(bp.Id, bp);
            return "^done,bkpt={number=" + Q(bp.Id) + ",addr=" + Q(verified ? Address(resolved) : "<PENDING>") +
                (verified ? ",fullname=" + Q(file) + ",line=" + Q(resolved.ToString(CultureInfo.InvariantCulture)) : "") + "}";
        }
        if (text.StartsWith("-break-delete ", StringComparison.Ordinal)) { breakpoints.Remove(text[14..]); return "^done"; }
        if (text == "-break-list") return "^done,BreakpointTable={body=[" + string.Join(',', breakpoints.Values.Select(b => "bkpt=" + BreakpointInfo(b))) + "]}";
        if (text.StartsWith("-break-enable ", StringComparison.Ordinal) || text.StartsWith("-break-disable ", StringComparison.Ordinal))
        {
            var key = text[(text.IndexOf(' ') + 1)..]; var bp = breakpoints[key]; var enable = text.StartsWith("-break-enable", StringComparison.Ordinal);
            if (enable && !bp.Enabled && bp.Verified && breakpoints.Values.Count(b => b.Enabled && b.Verified) >= 6) throw new InvalidOperationException("硬件断点数量已达到 6 个。");
            breakpoints[key] = bp with { Enabled = enable }; return "^done";
        }
        if (text.StartsWith("-exec-interrupt", StringComparison.Ordinal))
        {
            if (!running) throw new InvalidOperationException("目标尚未运行。");
            running = false; generation++; EmitStopped("signal-received"); return "^done";
        }
        if (text == "-interpreter-exec console \"monitor reset halt\"")
        {
            generation++; running = false; counter = input = output = doubled = 0; line = F407DebugExample.Entry; frame = 0; EmitStopped("reset"); return "^done";
        }
        if (text is "-exec-continue" or "-exec-next" or "-exec-step" or "-exec-finish")
        {
            if (running) throw new InvalidOperationException("目标正在运行，请先暂停。");
            if (text == "-exec-finish" && !InFunction) throw new InvalidOperationException("main 已是最外层栈帧，不能跳出。");
            running = true; frame = 0; var run = ++generation;
            RecordReceived?.Invoke("*running,thread-id=\"all\"");
            runner = RunAsync(text, run); return "^running";
        }
        if (running) throw new InvalidOperationException("目标运行时不读取寄存器、变量或内存。");
        if (text.StartsWith("-stack-select-frame ", StringComparison.Ordinal)) { frame = int.Parse(text[20..], CultureInfo.InvariantCulture); return "^done"; }
        if (text.StartsWith("-stack-list-frames", StringComparison.Ordinal))
            return "^done,stack=[" + Frame(0, InFunction ? "ComputeOutput" : "main", line) + (InFunction ? "," + Frame(1, "main", F407DebugExample.Call) : "") + "]";
        if (text == "-data-list-register-names") return "^done,register-names=[" + string.Join(',', RegisterNames.Select(Q)) + "]";
        if (text == "-data-list-register-values x") return "^done,register-values=[" + string.Join(',', RegisterNames.Select((name, i) => "{number=" + Q(i.ToString(CultureInfo.InvariantCulture)) + ",value=" + Q(Register(name)) + "}")) + "]";
        if (text.StartsWith("-stack-list-variables", StringComparison.Ordinal)) return "^done,variables=[" + (InFunction && frame == 0 ? Variable("input", input.ToString(CultureInfo.InvariantCulture)) + "," + Variable("doubled", line == F407DebugExample.Return ? doubled.ToString(CultureInfo.InvariantCulture) : "<尚未初始化>") : "") + "]";
        if (text.StartsWith("-data-evaluate-expression ", StringComparison.Ordinal))
        {
            var expression = MiRecord.Parse("~" + text[26..]).Data.Text!;
            var value = expression.StartsWith('$') && RegisterNames.Contains(expression[1..]) ? Register(expression[1..]) :
                DebugExpression.Parse(expression).Evaluate(symbol => Lookup(symbol, line, frame)).ToString(CultureInfo.InvariantCulture);
            return "^done,value=" + Q(value);
        }
        if (text.StartsWith("-data-read-memory-bytes ", StringComparison.Ordinal))
        {
            var parts = text.Split(' '); var address = Convert.ToUInt32(parts[1][2..], 16); var count = int.Parse(parts[2], CultureInfo.InvariantCulture);
            if (address < 0x20000000 || (ulong)address + (uint)count > 0x20000100) throw new InvalidOperationException("离线示例仅提供 0x20000000–0x200000FF 模拟 RAM。");
            var bytes = new byte[256]; BitConverter.GetBytes(counter).CopyTo(bytes, 0); BitConverter.GetBytes(input).CopyTo(bytes, 4); BitConverter.GetBytes(output).CopyTo(bytes, 8);
            var data = Convert.ToHexString(bytes.AsSpan((int)(address - 0x20000000), count)).ToLowerInvariant();
            return "^done,memory=[{begin=" + Q(parts[1]) + ",contents=" + Q(data) + "}]";
        }
        if (text.StartsWith("-gdb-set ", StringComparison.Ordinal)) return "^done";
        throw new InvalidOperationException("离线传输未实现该 MI 命令：" + text);
    }
    private async Task RunAsync(string action, int run)
    {
        try
        {
            do
            {
                await Task.Delay(action == "-exec-continue" ? 100 : 70, lifetime.Token);
                lock (sync)
                {
                    if (disposed || generation != run || !running) return;
                    Advance(action);
                    Breakpoint? hit = null;
                    foreach (var point in breakpoints.Values.Where(b => b.Enabled && b.Verified && b.Line == line).ToArray())
                    {
                        var skip = point.IgnoreRemaining > 0;
                        var updated = point with { HitCount = point.HitCount + 1, IgnoreRemaining = Math.Max(0, point.IgnoreRemaining - 1) };
                        breakpoints[point.Id] = updated;
                        RecordReceived?.Invoke("=breakpoint-modified,bkpt=" + BreakpointInfo(updated));
                        // 对齐 GDB ignore：先消耗跳过次数，这些次不求条件值。
                        if (!skip && (point.Condition.Length == 0 || DebugExpression.Parse(point.Condition).Evaluate(symbol => Lookup(symbol, line, 0)) != 0)) hit ??= updated;
                    }
                    if (action != "-exec-continue" || hit is not null)
                    {
                        running = false; EmitStopped(hit is null ? "end-stepping-range" : "breakpoint-hit", hit?.Id);
                        if (hit is { Temporary: true }) { breakpoints.Remove(hit.Id); RecordReceived?.Invoke("=breakpoint-deleted,id=" + Q(hit.Id)); }
                        return;
                    }
                }
            } while (true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (disposed || generation != run) return;
                running = false;
                RecordReceived?.Invoke("*stopped,reason=\"condition-error\",msg=" + Q(ex.Message) + ",frame={fullname=" + Q(F407DebugExample.RelativeFile) + ",line=" + Q(line.ToString(CultureInfo.InvariantCulture)) + "}");
            }
        }
    }
    private void Advance(string action)
    {
        if (action == "-exec-finish") { output = input * 2 + 1; line = F407DebugExample.Delay; return; }
        if (line == F407DebugExample.Entry) line = F407DebugExample.Increment;
        else if (line == F407DebugExample.Increment) { counter++; line = F407DebugExample.Input; }
        else if (line == F407DebugExample.Input) { input = counter & 0xff; line = F407DebugExample.Call; }
        else if (line == F407DebugExample.Call)
        {
            if (action is "-exec-step" or "-exec-continue") line = F407DebugExample.Function;
            else { output = input * 2 + 1; line = F407DebugExample.Delay; }
        }
        else if (line == F407DebugExample.Function) { doubled = input * 2; line = F407DebugExample.Return; }
        else if (line == F407DebugExample.Return) { output = doubled + 1; line = F407DebugExample.Delay; }
        else line = F407DebugExample.Increment;
    }
    private bool InFunction => line == F407DebugExample.Function || line == F407DebugExample.Return;
    private void EmitStopped(string reason, string? breakpoint = null) => RecordReceived?.Invoke("*stopped,reason=" + Q(reason) + (breakpoint is null ? "" : ",bkptno=" + Q(breakpoint)) + ",frame={fullname=" + Q(F407DebugExample.RelativeFile) + ",line=" + Q(line.ToString(CultureInfo.InvariantCulture)) + "}");
    private static string BreakpointInfo(Breakpoint point) => "{number=" + Q(point.Id) + ",times=" + Q(point.HitCount.ToString(CultureInfo.InvariantCulture)) + ",ignore=" + Q(point.IgnoreRemaining.ToString(CultureInfo.InvariantCulture)) + "}";
    private long Lookup(string symbol, int atLine, int selectedFrame) => symbol switch
    {
        "app_counter" => counter, "app_input" => input, "app_output" => output,
        "input" when selectedFrame == 0 && (atLine == F407DebugExample.Function || atLine == F407DebugExample.Return) => input,
        "doubled" when selectedFrame == 0 && atLine == F407DebugExample.Return => doubled,
        _ when symbol.StartsWith('$') && RegisterNames.Contains(symbol[1..]) => Convert.ToInt64(Register(symbol[1..])[2..], 16),
        _ => throw new InvalidOperationException("当前离线示例的作用域中没有此符号：" + symbol)
    };
    private static List<string> Arguments(string command)
    {
        var result = new List<string>();
        for (var i = 0; i < command.Length;)
        {
            if (char.IsWhiteSpace(command[i])) { i++; continue; }
            var start = i;
            if (command[i++] == '"')
            {
                while (i < command.Length)
                {
                    var c = command[i++];
                    if (c == '\\' && i < command.Length) { i++; continue; }
                    if (c == '"') break;
                }
                result.Add(MiRecord.Parse("~" + command[start..i]).Data.Text!);
            }
            else { while (i < command.Length && !char.IsWhiteSpace(command[i])) i++; result.Add(command[start..i]); }
        }
        return result;
    }
    private static string Frame(int level, string function, int number) => "frame={level=" + Q(level.ToString(CultureInfo.InvariantCulture)) + ",func=" + Q(function) + ",fullname=" + Q(F407DebugExample.RelativeFile) + ",line=" + Q(number.ToString(CultureInfo.InvariantCulture)) + ",addr=" + Q(Address(number)) + "}";
    private static string Variable(string name, string value) => "{name=" + Q(name) + ",type=\"uint32_t\",value=" + Q(value) + "}";
    private static string Address(int number) => "0x" + (0x08000100 + number * 4).ToString("x8", CultureInfo.InvariantCulture);
    private static readonly string[] RegisterNames = [..Enumerable.Range(0, 13).Select(i => "r" + i), "sp", "lr", "pc", "xpsr", "msp", "psp", "primask", "basepri", "faultmask", "control", "fpscr", ..Enumerable.Range(0, 32).Select(i => "s" + i)];
    private string Register(string name) => name switch
    {
        "r0" => "0x" + input.ToString("x8", CultureInfo.InvariantCulture), "r1" => "0x" + output.ToString("x8", CultureInfo.InvariantCulture),
        "sp" or "msp" => InFunction ? "0x2001ffe0" : "0x2001fff0", "lr" => InFunction ? Address(F407DebugExample.Delay) : "0xffffffff",
        "pc" => Address(line), "xpsr" => "0x01000000", _ => "0x00000000"
    };
    private static string Q(string value) => MiRecord.Quote(value);
    public async ValueTask DisposeAsync()
    {
        lock (sync) { disposed = true; running = false; generation++; lifetime.Cancel(); }
        await runner; lifetime.Dispose();
    }
}
