namespace StudioX.Engine.Debugging;

public sealed record SourceBreakpoint(string Id, string File, int Line, bool Enabled = true, bool Verified = false, string? Message = null,
    string Condition = "", int IgnoreCount = 0, bool Temporary = false, string? LogMessage = null,
    int HitCount = 0, int IgnoreRemaining = 0, bool SessionOnly = false, SourceLocation? BoundLocation = null)
{
    public string Kind => SessionOnly ? "运行到光标" : LogMessage is not null ? "日志" : Temporary ? "临时" : Condition.Length > 0 ? "条件" : IgnoreCount > 0 ? "计数" : "普通";
    public string Rule => string.Join(" · ", new[] { Condition, IgnoreCount > 0 ? $"跳过前 {IgnoreCount} 次" : "", Temporary ? "触发后删除" : "", LogMessage is not null ? "记录并继续" : "" }.Where(x => x.Length > 0));
}
