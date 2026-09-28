namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>分别确认 MCU 与引脚映射的完整回读校验，任一缺失都不能报告下载成功。</summary>
public static class Ag32PinMappingDownloadEvidence
{
    public static void Require(string output, IReadOnlyList<DownloadImageSnapshot> images,
        string logPath, bool truncated = false)
    {
        FirmwareVerificationEvidence.RequireNoFailure(output, logPath);
        if (truncated || images.Count != 2)
        {
            throw new StudioXException("AG32_MAPPING_VERIFY", "双映像校验日志不完整；请查看原始日志：" + logPath);
        }
        foreach (var image in images)
        {
            var marker = image.Preview.Role == "application" ? "APPLICATION" : "PIN_MAPPING";
            var blocks = Regex.Matches(output,
                @"^STUDIOX_VERIFY_" + marker + @"_BEGIN\r?\n(?<body>[\s\S]*?)^STUDIOX_VERIFY_" + marker + @"_END\r?$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (blocks.Count != 1)
            {
                throw new StudioXException("AG32_MAPPING_VERIFY", "缺少唯一的 " + image.Preview.Role + " 完整校验记录；原始日志：" + logPath);
            }
            FirmwareVerificationEvidence.Require(blocks[0].Groups["body"].Value, image.VerificationBytes, logPath);
        }
    }
}
