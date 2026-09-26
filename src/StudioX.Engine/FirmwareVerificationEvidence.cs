namespace StudioX.Engine;

using System.Globalization;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>调试附加和烧录均需要完整映像的实际校验输出，不能只信任工具退出状态。</summary>
public static class FirmwareVerificationEvidence
{
    public static void Require(string output, ulong expectedBytes, string logPath, bool truncated = false)
    {
        // WCH OpenOCD 在发现逐字节差异后仍可能返回成功；该响应不是匹配证据。
        var failure = new StudioXException("GDB_COMMAND", "OpenOCD 没有提供完整映像匹配证据。");
        if (new[] { "error reading USB data", "error writing USB data", "CMD_INFO failed", "CMD_CONNECT failed",
            "CMD_DISCONNECT failed", "LIBUSB_ERROR", "could not read product string", "error reading adapter response" }
            .Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            throw new StudioXException("DEBUG_IMAGE_VERIFY_TRANSPORT", "调试探针的 USB 通信中断，固件校验未完成；本次无法判断板上固件是否匹配。原始日志：" + logPath, failure);
        if (Regex.IsMatch(output, @"\bdiff\s+[0-9]+\s+address\s+0x", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new StudioXException("DEBUG_IMAGE_MISMATCH", "板上读回的固件字节与当前 ELF 不一致。请确认对应工程后再调试。原始日志：" + logPath, failure);
        var successes = Regex.Matches(output, @"^[ \t]*(?:OpenOCD:[ \t]*(?:Info[ \t]*:[ \t]*)?)?verified[ \t]+([0-9]+)[ \t]+bytes(?:[ \t]+in[^\r\n]*)?[ \t]*\r?$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var verified = expectedBytes > 0 && successes.Count > 0 && successes.All(match =>
            ulong.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes == expectedBytes);
        if (truncated || !verified)
            throw new StudioXException("DEBUG_IMAGE_VERIFY", "OpenOCD 未确认完整映像的校验字节数；不能将源码或 RTOS 数据视为板上程序的结果。原始日志：" + logPath, failure);
    }
}
