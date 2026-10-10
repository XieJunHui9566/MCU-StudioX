namespace StudioX.Application.Output;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>只解析显示信息；构建成败始终取实际构建报告，不由日志关键字决定。</summary>
public static partial class BuildOutputParser
{
    public static OutputTone Tone(string text)
    {
        text = HostTimestamp().Replace(text, "").TrimStart();
        if (text.StartsWith("[失败]", StringComparison.Ordinal))
        {
            return OutputTone.Error;
        }
        if (text.StartsWith("[取消]", StringComparison.Ordinal))
        {
            return OutputTone.Warning;
        }
        if (text.StartsWith("[成功]", StringComparison.Ordinal))
        {
            return OutputTone.Success;
        }
        if (text.StartsWith("[进度]", StringComparison.Ordinal) || text.StartsWith("[阶段]", StringComparison.Ordinal))
        {
            return OutputTone.Information;
        }
        if (Error().IsMatch(text) || IdfError().IsMatch(text) || FailedOperation().IsMatch(text) ||
            text.StartsWith("FAILED:", StringComparison.Ordinal) || text.Contains("undefined reference to", StringComparison.Ordinal) ||
            text.StartsWith("ninja: build stopped", StringComparison.Ordinal))
        {
            return OutputTone.Error;
        }
        if (Warning().IsMatch(text) || IdfWarning().IsMatch(text) || text.StartsWith("警告", StringComparison.Ordinal) ||
            CancelledOperation().IsMatch(text))
        {
            return OutputTone.Warning;
        }
        if (SuccessfulOperation().IsMatch(text) || text.StartsWith("ninja: no work to do", StringComparison.Ordinal))
        {
            return OutputTone.Success;
        }
        if (Measure(text) is not null || text.StartsWith("-- ", StringComparison.Ordinal) ||
            text.StartsWith("配置 ", StringComparison.Ordinal) || text.StartsWith("编译 ", StringComparison.Ordinal) || text.StartsWith("编译固件", StringComparison.Ordinal) ||
            text.StartsWith("检查开发环境", StringComparison.Ordinal) || text.StartsWith("校验开发环境", StringComparison.Ordinal) ||
            text.StartsWith("构建日志：", StringComparison.Ordinal))
        {
            return OutputTone.Information;
        }
        return OutputTone.Normal;
    }

    public static OutputMeasurement? Measure(string text)
    {
        var match = Count().Match(text.Trim());
        if (!match.Success)
        {
            match = ComponentCount().Match(text.Trim());
        }
        if (match.Success && long.TryParse(match.Groups["done"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var done) &&
            long.TryParse(match.Groups["total"].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var total) && total > 0 && done >= 0 && done <= total)
        {
            return new(done, total);
        }
        match = Percentage().Match(text.Trim());
        return match.Success && int.TryParse(match.Groups["percent"].Value, out var percent) && percent is >= 0 and <= 100 ? new(percent, 100, true) : null;
    }

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\]\s*", RegexOptions.CultureInvariant)] private static partial Regex HostTimestamp();
    [GeneratedRegex(@"(?:^|\s)(?:fatal\s+)?error(?:\s+[A-Z]*\d+)?\s*:|^FATAL:|^CMake Error(?:\s|:)|\b(?:\w*Exception|\w+Error):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Error();
    [GeneratedRegex(@"(?:^|\s)warning(?:\s+[A-Z]*\d+)?\s*:|^CMake Warning(?:\s|:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Warning();
    [GeneratedRegex(@"^E \(\d+\) \S+:", RegexOptions.CultureInvariant)] private static partial Regex IdfError();
    [GeneratedRegex(@"^W \(\d+\) \S+:", RegexOptions.CultureInvariant)] private static partial Regex IdfWarning();
    [GeneratedRegex(@"^(?:编译|映射编译|验证副本编译|CMake 配置|RTL 仿真|Verilog 综合|引脚映射编译|升级副本验证|PC 构建)失败", RegexOptions.CultureInvariant)] private static partial Regex FailedOperation();
    [GeneratedRegex(@"^(?:编译|映射编译|验证副本编译|CMake 配置|Verilog 综合|PC 构建)成功|^RTL 仿真完成", RegexOptions.CultureInvariant)] private static partial Regex SuccessfulOperation();
    [GeneratedRegex(@"^(?:编译|验证|CMake 配置|RTL 仿真|Verilog 综合|引脚映射编译|升级副本验证|PC 构建)已取消", RegexOptions.CultureInvariant)] private static partial Regex CancelledOperation();
    [GeneratedRegex(@"^\[(?<done>\d+)/(?<total>\d+)\](?:\s|$)", RegexOptions.CultureInvariant)] private static partial Regex Count();
    [GeneratedRegex(@"^校验开发环境组件 .*?\d+%[（(](?<done>[\d,]+)/(?<total>[\d,]+)[）)]", RegexOptions.CultureInvariant)] private static partial Regex ComponentCount();
    [GeneratedRegex(@"^\[(?<percent>\d{1,3})%\](?:\s|$)", RegexOptions.CultureInvariant)] private static partial Regex Percentage();
}
