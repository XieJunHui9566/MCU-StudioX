namespace StudioX.Engine.Debugging;

public sealed record BreakpointOptions(string Condition = "", int IgnoreCount = 0, bool Temporary = false, string? LogMessage = null)
{
    public static BreakpointOptions From(SourceBreakpoint point) => new(point.Condition, point.IgnoreCount, point.Temporary, point.LogMessage);
    public void Validate()
    {
        if (Condition is null || Condition.Length > 256 || IgnoreCount is < 0 or > 1000000)
        {
            throw new ArgumentException("条件最多 256 字符；跳过次数范围为 0–1000000。");
        }
        if (!string.IsNullOrWhiteSpace(Condition))
        {
            _ = DebugExpression.Parse(Condition);
        }
        if (LogMessage is not null)
        {
            _ = DebugLogTemplate.Parse(LogMessage);
        }
    }
}
