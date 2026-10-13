namespace StudioX.Application.Output;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>解析 ISP 显示阶段和已确认写入的字节数；最终结果仍取下载报告。</summary>
public static partial class StcIspOutputParser
{
    public static OutputActivity? Activity(string line)
    {
        var text = line.Trim();
        var progress = Progress().Match(text);
        if (progress.Success && long.TryParse(progress.Groups["done"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var done) &&
            long.TryParse(progress.Groups["total"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total) && total > 0 && done <= total)
        {
            return new("写入代码 Flash（字节）", new(done, total));
        }
        if (text.StartsWith("StudioX: waiting for MCU", StringComparison.Ordinal) || text.StartsWith("Waiting for MCU", StringComparison.Ordinal))
        {
            return new("等待 MCU 上电；请按开发板电源/下载键");
        }
        if (text.StartsWith("Protocol detected:", StringComparison.Ordinal))
        {
            return new("识别芯片与 ISP 协议");
        }
        if (text.StartsWith("STUDIOX_TARGET_VERIFIED ", StringComparison.Ordinal) || text.StartsWith("Trimming frequency:", StringComparison.Ordinal))
        {
            return new("核对目标与校准通信");
        }
        if (text.StartsWith("Erasing flash:", StringComparison.Ordinal))
        {
            return new("擦除代码 Flash");
        }
        if (text.StartsWith("Finishing write:", StringComparison.Ordinal) || text == "STUDIOX_PROGRAM_COMPLETE")
        {
            return new("核对写入结束应答");
        }
        if (text.StartsWith("Setting options:", StringComparison.Ordinal))
        {
            return new("保存芯片选项");
        }
        return null;
    }

    [GeneratedRegex(@"^STUDIOX_ISP_PROGRESS (?<done>\d+) (?<total>\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Progress();
}
