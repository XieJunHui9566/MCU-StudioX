namespace StudioX.Engine;

public sealed record StcIspReport(bool Success, string Log, string LogPath, int ExitCode, bool TimedOut, bool ModelVerified)
{
    public string Summary => Success
        ? "STC ISP 报告写入完成并已退出；此工具不执行 Flash 读回校验，请观察板上程序运行。"
        : TimedOut ? "STC ISP 等待或写入超时；请查看日志确认芯片状态。" :
            "STC ISP 未完成；若已开始擦写，板内程序可能不完整，请查看原始日志。";
}
