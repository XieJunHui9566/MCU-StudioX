namespace StudioX.Engine.Arduino;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>保留上传工具的原始结果与日志；AVR 成功还须具有实际 Flash 校验记录。</summary>
public sealed record ArduinoUploadReport(ProcessResult Result, string LogPath, bool Verified)
{
    public bool Success => Result.Success && !Result.OutputTruncated && Verified;

    public string Summary => Success ? "Arduino 上传成功，固件已校验，退出码 0。" :
        $"Arduino 上传失败，退出码 {Result.ExitCode}" + (Result.TimedOut ? "（工具执行超时）" :
        Result.OutputTruncated ? "（工具日志不完整）" : Result.Success ? "（缺少固件校验证据）" : "") + "；请查看上传日志。";

    public static bool HasVerification(ArduinoBoard board, ProcessResult result) => result.Success && !result.OutputTruncated &&
        (board.PlatformId == "avr" ? Regex.IsMatch(result.StandardOutput + "\n" + result.StandardError,
            @"\b[1-9][0-9]* bytes? of flash verified\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) :
            (result.StandardOutput + "\n" + result.StandardError).Contains("Hash of data verified", StringComparison.OrdinalIgnoreCase));
}
