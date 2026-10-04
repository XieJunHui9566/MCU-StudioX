namespace StudioX.Engine;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>只修改可完整表达的行；其余原文、注释与每行换行保持不变。</summary>
internal sealed class Ag32PinPlanDocument
{
    private sealed record Line(string Text, string Ending, Ag32PinAssignment? Assignment, string? Clock);
    private readonly Line[] lines;
    private readonly bool bom;
    private readonly string newline;
    internal Ag32PinAssignment[] Assignments
    {
        get;
    }
    internal Ag32PinClockSettings Clocks
    {
        get;
    }
    internal List<string> Diagnostics { get; } = [];
    internal bool CanEdit => Diagnostics.Count == 0;

    internal Ag32PinPlanDocument(byte[] bytes)
    {
        bom = bytes is [0xef, 0xbb, 0xbf, ..];
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bom ? bytes[3..] : bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new StudioXException("AG32_PIN_PLAN_ENCODING", "图形规划需要有效的 UTF-8 VE 文件。", ex);
        }
        var parsed = new List<Line>();
        var clocks = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var number = 0;
        foreach (Match match in Regex.Matches(text, @"([^\r\n]*)(\r\n|\r|\n|$)", RegexOptions.CultureInvariant))
        {
            if (match.Length == 0)
            {
                continue;
            }
            number++;
            var original = match.Groups[1].Value;
            var content = original.Split('#', 2)[0].Trim();
            var clock = Regex.Match(content, @"^(HSECLK|SYSCLK|BUSCLK)\s+(\S+)\s*$", RegexOptions.CultureInvariant);
            var assignment = Regex.Match(content, @"^([A-Z][A-Z0-9_]*)\s+PIN_([0-9]+)(?::(INPUT|OUTPUT|INOUT))?\s*$", RegexOptions.CultureInvariant);
            Ag32PinAssignment? mapping = null;
            string? clockName = null;
            if (clock.Success)
            {
                clockName = clock.Groups[1].Value;
                if (!decimal.TryParse(clock.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                    !clocks.TryAdd(clockName, value))
                {
                    Diagnostics.Add($"第 {number} 行：时钟数值无效或重复，保留原文并只读显示。");
                }
            }
            else if (assignment.Success && int.TryParse(assignment.Groups[2].Value, out var pin))
            {
                var electrical = Ag32GpioElectrical.ReadOptions(original);
                mapping = new(assignment.Groups[1].Value, pin,
                    assignment.Groups[3].Success ? assignment.Groups[3].Value : null,
                    ReadName(original), electrical.Pull, electrical.OutputType);
            }
            else if (content.Length != 0 && !IsPreservedSetting(content))
            {
                Diagnostics.Add($"第 {number} 行：包含自定义或复杂 VE 配置；请在文本编辑器处理，图形规划只读。");
            }
            if (mapping is null && original.Contains(Ag32GpioElectrical.Marker, StringComparison.Ordinal))
            {
                throw new StudioXException("AG32_GPIO_OPTIONS", $"第 {number} 行的电气配置需绑定一个简单 GPIO 到 PIN 的映射。");
            }
            parsed.Add(new(original, match.Groups[2].Value, mapping, clockName));
        }
        lines = parsed.ToArray();
        newline = lines.FirstOrDefault(line => line.Ending.Length != 0)?.Ending ?? "\n";
        Assignments = lines.Where(line => line.Assignment is not null).Select(line => line.Assignment!).ToArray();
        Clocks = new(GetClock("HSECLK"), GetClock("SYSCLK"), GetClock("BUSCLK"));
        decimal? GetClock(string name) => clocks.TryGetValue(name, out var value) ? value : null;
    }

    internal byte[] Render(IReadOnlyList<Ag32PinAssignment> assignments, Ag32PinClockSettings clocks)
    {
        if (!CanEdit)
        {
            throw new StudioXException("AG32_PIN_PLAN_READONLY", string.Join("\n", Diagnostics));
        }
        var remaining = assignments.ToList();
        var clockValues = new Dictionary<string, decimal?>(StringComparer.Ordinal)
        {
            ["HSECLK"] = clocks.HseMhz,
            ["SYSCLK"] = clocks.SysMhz,
            ["BUSCLK"] = clocks.BusMhz
        };
        var result = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Assignment is { } old)
            {
                var index = remaining.FindIndex(item => item.Function == old.Function);
                if (index < 0)
                {
                    index = remaining.FindIndex(item => item.PinNumber == old.PinNumber);
                }
                if (index < 0)
                {
                    result.Append(CommentOnly(RemoveName(Ag32GpioElectrical.RemoveOptions(line.Text)))).Append(line.Ending);
                    continue;
                }
                var value = remaining[index];
                remaining.RemoveAt(index);
                if (value == old)
                {
                    result.Append(line.Text);
                }
                else
                {
                    result.Append(Ag32GpioElectrical.WithOptions(WithName(ReplaceContent(RemoveName(Ag32GpioElectrical.RemoveOptions(line.Text)), RenderAssignment(value)), value.Name), value));
                }
            }
            else if (line.Clock is { } name)
            {
                var value = clockValues[name];
                clockValues.Remove(name);
                var oldValue = name switch
                {
                    "HSECLK" => Clocks.HseMhz,
                    "SYSCLK" => Clocks.SysMhz,
                    _ => Clocks.BusMhz
                };
                if (value == oldValue)
                {
                    result.Append(line.Text);
                }
                else if (value is { } frequency)
                {
                    result.Append(ReplaceContent(line.Text, name + " " + Number(frequency)));
                }
                else
                {
                    result.Append(CommentOnly(line.Text));
                }
            }
            else
            {
                result.Append(line.Text);
            }
            result.Append(line.Ending);
        }
        foreach (var mapping in remaining)
        {
            AppendLine(Ag32GpioElectrical.WithOptions(WithName(RenderAssignment(mapping), mapping.Name), mapping));
        }
        foreach (var (name, value) in clockValues)
        {
            if (value is { } frequency)
            {
                AppendLine(name + " " + Number(frequency));
            }
        }
        var body = Encoding.UTF8.GetBytes(result.ToString());
        return bom ? [0xef, 0xbb, 0xbf, .. body] : body;

        void AppendLine(string content)
        {
            if (result.Length != 0 && result[^1] is not ('\r' or '\n'))
            {
                result.Append(newline);
            }
            result.Append(content).Append(newline);
        }
    }

    private static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string? ReadName(string line)
    {
        var comment = line.IndexOf('#');
        if (comment < 0)
        {
            return null;
        }
        var name = Regex.Match(line[comment..], @"^#([A-Za-z][A-Za-z0-9_]*)(?=\s*(?:#|$))", RegexOptions.CultureInvariant);
        return name.Success ? name.Groups[1].Value : null;
    }

    private static string RemoveName(string line)
    {
        var name = ReadName(line);
        return name is null ? line : line.Remove(line.IndexOf('#'), name.Length + 1).TrimEnd();
    }
    private static string WithName(string line, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return line;
        }
        var comment = line.IndexOf('#');
        return comment < 0 ? line.TrimEnd() + " #" + name : line.Insert(comment, "#" + name + " ");
    }

    private static bool IsPreservedSetting(string content)
    {
        // 保留简单厂商参数，不把任意尾随内容带入后续 VEX/Tcl 或 Verilog。复杂配置仍可用文本编辑器。
        return Regex.IsMatch(content,
            @"^(?:PLLINCLK|PLLPFD|PLLVCO|PLLCLK[0-4](?:_PHASE)?)\s+[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\s*$",
            RegexOptions.CultureInvariant) ||
            Regex.IsMatch(content, @"^USB0\s+(?:[Hh][Oo][Ss][Tt]|[Dd][Ee][Vv][Ii][Cc][Ee]|[Oo][Tt][Gg])\s*$", RegexOptions.CultureInvariant) ||
            Regex.IsMatch(content, @"^TEST_MODE\s+(?:[0-3]|2'[bB][01]{1,2})\s*$", RegexOptions.CultureInvariant);
    }
    private static string RenderAssignment(Ag32PinAssignment value)
        => value.Function + " PIN_" + value.PinNumber.ToString(CultureInfo.InvariantCulture)
            + (value.Direction is { Length: > 0 } direction ? ":" + direction : "");

    private static string CommentOnly(string original)
    {
        var comment = original.IndexOf('#');
        return comment < 0 ? "" : new string(original.TakeWhile(char.IsWhiteSpace).ToArray()) + original[comment..];
    }

    private static string ReplaceContent(string original, string content)
    {
        var comment = original.IndexOf('#');
        var code = comment >= 0 ? original[..comment] : original;
        var leading = new string(code.TakeWhile(char.IsWhiteSpace).ToArray());
        var trailing = new string(code.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray());
        return leading + content + trailing + (comment >= 0 ? original[comment..] : "");
    }
}
