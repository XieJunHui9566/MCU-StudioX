namespace StudioX.Engine.Pic;

using StudioX.Foundation;

/// <summary>原始 IPECMD 结果、回读备份和日志；成功要求工具退出成功且存在校验完成记录。</summary>
public sealed record PicDownloadReport(bool Success, ProcessResult Result, string LogPath, string BackupPath)
{
    public string Summary => Success ? "PICkit3 下载成功，程序与配置已校验。" :
        $"PICkit3 下载失败或未完成校验，退出码 {Result.ExitCode}" + (Result.TimedOut ? "（工具超时）" : "") + "；请查看原始日志。";
}
