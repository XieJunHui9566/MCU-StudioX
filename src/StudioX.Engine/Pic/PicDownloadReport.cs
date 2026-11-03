namespace StudioX.Engine.Pic;

using StudioX.Foundation;

/// <summary>原始 IPECMD 结果和日志；普通下载不生成原程序备份，成功要求内置写后校验完成。</summary>
public sealed record PicDownloadReport(bool Success, ProcessResult Result, string LogPath, string? BackupPath = null)
{
    public string Summary => Success ? "PICkit3 下载成功，程序与配置已校验。" :
        $"PICkit3 下载失败或未完成校验，退出码 {Result.ExitCode}" + (Result.TimedOut ? "（工具超时）" : "") + "；请查看原始日志。";
}
