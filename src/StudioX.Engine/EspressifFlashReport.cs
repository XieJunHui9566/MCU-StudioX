namespace StudioX.Engine;

public sealed record EspressifFlashReport(bool Success, string Log, string LogPath, int ExitCode, bool TimedOut)
{
    public string Summary => $"ESP 下载{(Success ? "成功 · 全部映像已校验并复位运行" : "失败 · 请查看 esptool 原始日志")}，退出代码：{ExitCode}" +
        (TimedOut ? "（工具执行超时）" : "");
}
