namespace StudioX.Engine;

using StudioX.Foundation;

/// <summary>根据原始诊断区分连接和写入故障；只有明确未完成连接的失败可考虑重连。</summary>
public static class OpenOcdDownloadDiagnostics
{
    public const string ConnectionBegin = "STUDIOX_CONNECT_BEGIN";
    public const string ConnectionReady = "STUDIOX_CONNECT_READY";
    public const string FlashWriteBegin = "STUDIOX_FLASH_WRITE_BEGIN";

    /// <summary>只用于内置 AG32 双映像流程；固定探针序列号，避免重连时选中另一台设备。</summary>
    public static bool CanRetryConnection(DownloadPreparation preparation, ProcessResult result)
    {
        var text = result.StandardOutput + "\n" + result.StandardError;
        return preparation.Configuration.Device.Id == "AG32VF303CCT6" && preparation.Images.Count == 2 &&
            preparation.Images[0].Preview.Role == "application" && preparation.Images[1].Preview.Role == "pin-mapping" &&
            preparation.Options.ProbeId is "cmsis-dap" or "agm-blaster" && !string.IsNullOrWhiteSpace(preparation.Options.Serial) &&
            result.ExitCode == 1 && !result.TimedOut && !result.OutputTruncated &&
            HasLine(text, ConnectionBegin) && !text.Contains(ConnectionReady, StringComparison.Ordinal) &&
            !text.Contains(FlashWriteBegin, StringComparison.Ordinal) &&
            !text.Contains("STUDIOX_VERIFY_", StringComparison.Ordinal) &&
            !text.Contains("STUDIOX_DOWNLOAD_VERIFIED", StringComparison.Ordinal) &&
            !ContainsAny(text, "auto erase enabled", "wrote ", "verified ", "Assertion failed", "read protection", "Expected AG32") &&
            ContainsAny(text, "Error connecting DP: cannot read IDR", "unable to find a matching CMSIS-DAP device");
    }

    public static string FailureSummary(string log)
    {
        // 保留全部原始日志；摘要用于状态栏和 MCP，让 exit=1 对应到实际失败原因。
        if (ContainsAny(log, "could not read product string", "error reading USB data", "error writing USB data", "LIBUSB_ERROR"))
        {
            return "烧录器 USB 通信失败，请检查 USB 连接、驱动及其它调试软件占用";
        }
        if (ContainsAny(log, "unable to find a matching CMSIS-DAP device"))
        {
            return "未找到匹配的 CMSIS-DAP，请检查连接、驱动及所设序列号";
        }
        if (ContainsAny(log, "Error connecting DP: cannot read IDR", "Unable to execute DAP queue"))
        {
            return "DAP 与目标通信失败，请检查目标供电、SWD 接线，或降低下载速度";
        }
        if (ContainsAny(log, "Assertion failed"))
        {
            return "OpenOCD 异常退出，写入状态未确认，请查看原始日志";
        }
        if (ContainsAny(log, "read protection enabled"))
        {
            return "目标已启用读保护，下载已停止";
        }
        if (ContainsAny(log, "Expected AG32", "AG32 logic layout differs"))
        {
            return "目标型号、Flash 容量或逻辑区布局与工程不匹配";
        }
        return "请查看 OpenOCD 日志";
    }

    private static bool HasLine(string text, string marker) => text.Split('\n').Any(line => line.Trim() == marker);

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));
}
