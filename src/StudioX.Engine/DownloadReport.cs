namespace StudioX.Engine;

public sealed record DownloadReport(bool Success, string Log, string LogPath, int ExitCode, bool TimedOut)
{
    public string Summary => $"下载{(Success ? "成功" : "失败")}，退出代码：{ExitCode}" +
        (Success ? " · 已校验并复位运行" : TimedOut ? "（工具执行超时）" : " · 请查看 OpenOCD 日志");
}
