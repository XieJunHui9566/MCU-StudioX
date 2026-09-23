namespace StudioX.Engine.Debugging;

public enum DebugState { Disconnected, Starting, Stopped, Running, Stopping, Faulted }
public enum DebugAction { Continue, Pause, StepInto, StepOver, StepOut, Reset }
public sealed record SourceLocation(string File, int Line);
public sealed record SourceBreakpoint(string Id, string File, int Line, bool Enabled = true, bool Verified = false, string? Message = null,
    string Condition = "", int IgnoreCount = 0, bool Temporary = false, string? LogMessage = null,
    int HitCount = 0, int IgnoreRemaining = 0, bool SessionOnly = false, SourceLocation? BoundLocation = null)
{
    public string Kind => SessionOnly ? "运行到光标" : LogMessage is not null ? "日志" : Temporary ? "临时" : Condition.Length > 0 ? "条件" : IgnoreCount > 0 ? "计数" : "普通";
    public string Rule => string.Join(" · ", new[] { Condition, IgnoreCount > 0 ? $"跳过前 {IgnoreCount} 次" : "", Temporary ? "触发后删除" : "", LogMessage is not null ? "记录并继续" : "" }.Where(x => x.Length > 0));
}
public sealed record BreakpointOptions(string Condition = "", int IgnoreCount = 0, bool Temporary = false, string? LogMessage = null)
{
    public static BreakpointOptions From(SourceBreakpoint point) => new(point.Condition, point.IgnoreCount, point.Temporary, point.LogMessage);
    public void Validate()
    {
        if (Condition is null || Condition.Length > 256 || IgnoreCount is < 0 or > 1000000) throw new ArgumentException("条件最多 256 字符；跳过次数范围为 0–1000000。");
        if (!string.IsNullOrWhiteSpace(Condition)) _ = DebugExpression.Parse(Condition);
        if (LogMessage is not null) _ = DebugLogTemplate.Parse(LogMessage);
    }
}
public sealed record DebugRegister(string Name, string Value, bool Changed = false);
public sealed record DebugVariable(string Name, string Value, string Type = "", bool Changed = false);
public sealed record DebugFrame(int Level, string Function, string File, int Line, string Address);
public sealed record DebugSnapshot(DebugRegister[] Registers, DebugFrame[] Frames, DebugVariable[] Locals, DebugVariable[] Watches, int SelectedFrame = 0)
{
    public static DebugSnapshot Empty { get; } = new([], [], [], []);
}

/// <summary>传输边界：UI 不执行 GDB 文本，不持有进程。离线传输与未来进程传输共用 MI 解析和命令映射。</summary>
public interface IGdbMiTransport : IAsyncDisposable
{
    event Action<string>? RecordReceived;
    Task<string> ExecuteAsync(string command, CancellationToken token = default);
}
