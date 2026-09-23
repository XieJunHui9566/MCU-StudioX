namespace StudioX.Engine.Debugging;

using System.Globalization;
using StudioX.Foundation;

/// <summary>所有调试交互映射到标准 MI；未提供任意命令输入，避免观察窗口意外执行赋值/函数调用。</summary>
public sealed class GdbDebugAdapter(IGdbMiTransport transport, DebugTargetProfile? target = null) : IAsyncDisposable
{
    public event Action<string>? RecordReceived { add => transport.RecordReceived += value; remove => transport.RecordReceived -= value; }
    public event Action<string>? Trace;
    private int nextToken;
    public async Task<MiValue> SendAsync(string command, CancellationToken token = default)
    {
        var number = Interlocked.Increment(ref nextToken);
        Trace?.Invoke($"> {number}{command}");
        var text = await transport.ExecuteAsync(number.ToString(CultureInfo.InvariantCulture) + command, token);
        Trace?.Invoke("< " + text);
        var response = MiRecord.Parse(text);
        if (response.Kind != '^' || response.Token != number) throw new StudioXException("GDB_PROTOCOL", "GDB 返回了不匹配的命令响应。");
        if (response.Class == "error") throw new StudioXException("GDB_COMMAND", response.Data.String("msg", "GDB 命令失败。"));
        if (response.Class is not ("done" or "running" or "connected" or "exit")) throw new StudioXException("GDB_PROTOCOL", "未知 GDB 响应：" + response.Class);
        return response.Data;
    }
    public async Task<(string Number, bool Verified, SourceLocation? Location)> InsertAsync(SourceBreakpoint point, CancellationToken token)
    {
        BreakpointOptions.From(point).Validate();
        // 显式硬件断点用于 Flash 源码；pending 留给未解析到符号的源文件，不伪装成已绑定。
        var result = await SendAsync("-break-insert -h -f " + (!point.Enabled ? "-d " : "") + (point.Temporary ? "-t " : "") +
            (point.IgnoreCount > 0 ? "-i " + point.IgnoreCount.ToString(CultureInfo.InvariantCulture) + " " : "") +
            (point.Condition.Length > 0 ? "-c " + MiRecord.Quote(point.Condition) + " " : "") +
            MiRecord.Quote(point.File + ":" + point.Line.ToString(CultureInfo.InvariantCulture)), token);
        var breakpoint = result.Get("bkpt") ?? throw new StudioXException("GDB_PROTOCOL", "缺少断点响应。");
        var verified = breakpoint.String("addr") != "<PENDING>";
        return (breakpoint.String("number"), verified, verified ? BreakpointLocation(breakpoint, point.File) : null);
    }
    public static SourceLocation? BreakpointLocation(MiValue info, string fallbackFile)
    {
        if (!int.TryParse(info.String("line"), NumberStyles.None, CultureInfo.InvariantCulture, out var line) || line < 1) return null;
        var file = info.String("fullname");
        if (string.IsNullOrWhiteSpace(file)) file = info.String("file");
        return new(string.IsNullOrWhiteSpace(file) ? fallbackFile : file, line);
    }
    public async Task<string> EvaluateAsync(string expression, CancellationToken token = default)
    {
        _ = DebugExpression.Parse(expression);
        return (await SendAsync("-data-evaluate-expression " + MiRecord.Quote(expression), token)).String("value");
    }
    public async Task<MiValue[]> BreakpointInfoAsync(CancellationToken token = default) =>
        (await SendAsync("-break-list", token)).Get("BreakpointTable")?.Get("body")?.Values.ToArray() ?? [];
    public Task<MiValue> DeleteAsync(string id, CancellationToken token) => SendAsync("-break-delete " + Id(id), token);
    public Task<MiValue> EnableAsync(string id, bool enabled, CancellationToken token) => SendAsync((enabled ? "-break-enable " : "-break-disable ") + Id(id), token);
    private static string Id(string id) => id.Length > 0 && id.All(c => char.IsAsciiDigit(c) || c == '.') ? id : throw new FormatException("Invalid GDB breakpoint ID.");
    public Task<MiValue> ActionAsync(DebugAction action, CancellationToken token) => SendAsync(action switch
    {
        DebugAction.Continue => "-exec-continue", DebugAction.Pause => "-exec-interrupt --all",
        DebugAction.StepInto => "-exec-step", DebugAction.StepOver => "-exec-next", DebugAction.StepOut => "-exec-finish",
        DebugAction.Reset => "-interpreter-exec console \"monitor reset halt\"", _ => throw new ArgumentOutOfRangeException(nameof(action))
    }, token);
    public async Task<DebugSnapshot> ReadAsync(IReadOnlyList<string> watches, int frame, CancellationToken token)
    {
        var stack = await SendAsync("-stack-list-frames 0 31", token);
        var frames = (stack.Get("stack")?.Values ?? []).Select(x => new DebugFrame(Number(x.String("level")), x.String("func", "??"), x.String("fullname", x.String("file")), Number(x.String("line")), x.String("addr"))).ToArray();
        if (!frames.Any(x => x.Level == frame)) frame = 0;
        await SendAsync("-stack-select-frame 0", token);
        var names = (await SendAsync("-data-list-register-names", token)).Get("register-names")?.Values.Select(x => x.Text ?? "").ToArray() ?? [];
        var selected = target?.IsWch == true
            ? names.Select((name, index) => (name, index)).Where(item => WchDebugTarget.IsVisibleRegister(item.name, target.HasFpu)).Select(item => item.index).ToArray()
            : null;
        if (selected is { Length: 0 }) throw new StudioXException("GDB_REGISTERS", "GDB 未返回当前 WCH 型号可识别的内核寄存器。");
        var values = (await SendAsync("-data-list-register-values x" + (selected is null ? "" : " " + string.Join(' ', selected)), token)).Get("register-values")?.Values ?? [];
        var registers = values.Select(x => (Number: Number(x.String("number", "-1")), Value: x.String("value")))
            .Where(x => x.Number >= 0 && x.Number < names.Length && names[x.Number].Length > 0 && (selected is null || selected.Contains(x.Number)))
            .Select(x => new DebugRegister(names[x.Number], x.Value)).ToArray();
        await SendAsync("-stack-select-frame " + frame.ToString(CultureInfo.InvariantCulture), token);
        var locals = (await SendAsync("-stack-list-variables --simple-values", token)).Get("variables")?.Values.Select(x => new DebugVariable(x.String("name"), x.String("value", "<展开暂未接入>"), x.String("type"))).ToArray() ?? [];
        var watched = new List<DebugVariable>();
        foreach (var expression in watches)
        {
            try { watched.Add(new(expression, (await SendAsync("-data-evaluate-expression " + MiRecord.Quote(expression), token)).String("value"))); }
            catch (StudioXException ex) when (ex.Code == "GDB_COMMAND") { watched.Add(new(expression, "不可用：" + ex.Message)); }
        }
        return new(registers, frames, locals, watched.ToArray(), frame);
    }
    public async Task<string> ReadMemoryAsync(uint address, int count, CancellationToken token)
    {
        if (count is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(count));
        var memory = (await SendAsync($"-data-read-memory-bytes 0x{address:x8} {count}", token)).Get("memory")?.Values.FirstOrDefault();
        return memory?.String("contents") ?? throw new StudioXException("GDB_MEMORY", "没有返回内存数据。");
    }
    private static int Number(string value) => int.TryParse(value, CultureInfo.InvariantCulture, out var number) ? number : 0;
    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
