namespace StudioX.Engine;

public sealed record BuildReport(bool Success, string Log, IReadOnlyList<string> Artifacts, string? LogPath = null, int? ExitCode = null, bool TimedOut = false)
{
    public string Summary => $"{(Success ? "编译成功" : "编译失败")}，退出代码：{(ExitCode is { } code ? code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "不可用")}{(TimedOut ? "（工具执行超时）" : "")}";
}
