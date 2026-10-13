namespace StudioX.Application.Output;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>输出区内的字符进度条；阶段测量与最终构建结果分别呈现。</summary>
public static partial class TerminalProgressFormatter
{
    public static string Phase(string text)
    {
        var component = Component().Match(text);
        if (component.Success)
        {
            return "校验开发环境组件 " + component.Groups["id"].Value + " / " + component.Groups["version"].Value;
        }
        return text.Trim().TrimEnd('…');
    }

    public static string Render(string phase, OutputMeasurement? measurement, TimeSpan? elapsed = null, int frame = 0, string state = "进度")
    {
        const int width = 24;
        string bar;
        if (measurement is { } count)
        {
            var filled = Math.Clamp((int)(count.Percent * width / 100), 0, width);
            bar = new string('=', filled) + (filled < width ? ">" + new string('-', width - filled - 1) : "");
        }
        else
        {
            var position = Math.Abs(frame % (width - 2));
            bar = new string(' ', position) + "<=>" + new string(' ', width - position - 3);
        }
        var detail = measurement is null ? "" : " " + measurement.Percent.ToString("0", CultureInfo.InvariantCulture) + "%" +
            (measurement.PercentageOnly ? "" : $" ({measurement.Completed.ToString("N0", CultureInfo.InvariantCulture)}/{measurement.Total.ToString("N0", CultureInfo.InvariantCulture)})");
        var time = elapsed is { } duration ? " · " + duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) : "";
        return $"[{state}] {phase} [{bar}]{detail}{time}";
    }

    /// <summary>累计 PC 日志的显示副本；原始快照和构建日志不受合并影响。</summary>
    public static string CompactLog(string raw, bool building, string? result = null)
    {
        var lines = new List<string>();
        var active = -1;
        var phase = "PC 构建";
        OutputMeasurement? measurement = null;
        void Activity(string next, OutputMeasurement? count)
        {
            if (next != phase && active >= 0 && measurement is not null)
            {
                lines[active] = Render(phase, measurement, state: "阶段");
                active = -1;
            }
            phase = next;
            measurement = count;
            if (active < 0)
            {
                active = lines.Count;
                lines.Add("");
            }
            lines[active] = Render(phase, measurement);
        }
        var buffer = new OutputLineBuffer();
        foreach (var text in buffer.Append(raw).Concat(buffer.Flush() is { } tail ? [tail] : Array.Empty<string>()))
        {
            if (text.StartsWith("校验开发环境组件 ", StringComparison.Ordinal) || text.StartsWith("完整校验开发环境组件 ", StringComparison.Ordinal))
            {
                Activity(Phase(text), BuildOutputParser.Measure(text));
                continue;
            }
            if (text.Trim() is "[PC 配置]" or "[PC 编译]")
            {
                Activity(text.Trim()[1..^1], null);
                continue;
            }
            // 编译任务行包含文件名和命令线索，保留正文；仅重复的组件百分比合并。
            if (active < 0)
            {
                lines.Add(text);
            }
            else
            {
                lines.Insert(active++, text);
            }
            if (BuildOutputParser.Measure(text) is { } count)
            {
                Activity(phase, count);
            }
        }
        if (active >= 0 && !building)
        {
            lines[active] = Render(phase, measurement, state: "阶段");
        }
        if (result is not null)
        {
            if (active >= 0)
            {
                lines[active] = result;
            }
            else
            {
                lines.Add(result);
            }
        }
        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"开发环境组件[：\s]+(?<id>[^\s/]+)\s*/\s*(?<version>[\w.\-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Component();
}
