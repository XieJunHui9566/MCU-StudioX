namespace StudioX.Engine;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>GPIO 电气选项属于逻辑 IO 单元；由 VE 注释持久化并生成厂商 ASF，不能伪造 MCU 寄存器 API。</summary>
internal static class Ag32GpioElectrical
{
    internal const string FileName = "studiox-gpio.asf";
    internal const string Marker = "#@StudioX:GPIO";
    internal static bool IsGpio(string function) => Regex.IsMatch(function, @"\AGPIO[0-9]+_[0-7]\z", RegexOptions.CultureInvariant);
    internal static bool IsValid(Ag32PinAssignment pin) => pin.Pull is "NONE" or "UP" or "DOWN" or "HOLD" &&
        pin.OutputType is "PUSH_PULL" or "OPEN_DRAIN" &&
        (IsGpio(pin.Function) || pin.Pull == "NONE" && pin.OutputType == "PUSH_PULL") &&
        (pin.Direction != "INPUT" || pin.OutputType == "PUSH_PULL") &&
        (!(pin.InitialHigh || pin.SlewControl) || IsGpio(pin.Function) && pin.Direction == "OUTPUT") &&
        pin.InterruptPriority is >= 1 and <= 15 &&
        (pin.Interrupt == "NONE" || IsGpio(pin.Function) && pin.Direction == "INPUT" &&
            pin.Interrupt is "RISEEDGE" or "FALLEDGE" or "BOTHEDGE" or "HIGHLEVEL" or "LOWLEVEL");

    internal static Ag32PinAssignment ReadOptions(string line, Ag32PinAssignment assignment)
    {
        var index = line.IndexOf(Marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return assignment;
        }
        var match = Regex.Match(line[index..], @"\A#@StudioX:GPIO pull=(NONE|UP|DOWN|HOLD) output=(PUSH_PULL|OPEN_DRAIN)(?: initial=(LOW|HIGH) slew=([01]) irq=(NONE|RISEEDGE|FALLEDGE|BOTHEDGE|HIGHLEVEL|LOWLEVEL)(?: priority=(1[0-5]|[1-9]))?)?[ \t]*\z", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            throw new StudioXException("AG32_GPIO_OPTIONS", "GPIO 电气配置注释无效：" + line[index..]);
        }
        return assignment with
        {
            Pull = match.Groups[1].Value,
            OutputType = match.Groups[2].Value,
            InitialHigh = match.Groups[3].Value == "HIGH",
            SlewControl = match.Groups[4].Value == "1",
            Interrupt = match.Groups[5].Success ? match.Groups[5].Value : "NONE",
            InterruptPriority = match.Groups[6].Success ? int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture) : 1
        };
    }

    internal static string RemoveOptions(string line)
    {
        var index = line.IndexOf(Marker, StringComparison.Ordinal);
        return index < 0 ? line : line[..index].TrimEnd();
    }
    internal static string WithOptions(string line, Ag32PinAssignment pin)
    {
        var extended = pin.InitialHigh || pin.SlewControl || pin.Interrupt != "NONE";
        if (!extended && pin.Pull == "NONE" && pin.OutputType == "PUSH_PULL")
        {
            return line;
        }
        var options = line.TrimEnd() + $" {Marker} pull={pin.Pull} output={pin.OutputType}";
        return extended ? options + $" initial={(pin.InitialHigh ? "HIGH" : "LOW")} slew={(pin.SlewControl ? 1 : 0)} irq={pin.Interrupt}" +
            (pin.InterruptPriority == 1 ? "" : $" priority={pin.InterruptPriority}") : options;
    }
    internal static string DescribePull(Ag32PinAssignment pin) =>
        pin.Pull switch
        {
            "UP" => "上拉",
            "DOWN" => "下拉",
            "HOLD" => "总线保持",
            _ => "无上下拉"
        };
    internal static string Describe(Ag32PinAssignment pin) =>
        DescribePull(pin) + " / " +
        (pin.OutputType == "OPEN_DRAIN" ? "开漏" : "推挽");

    internal static async Task CreateAsync(string build, byte[] source, CancellationToken token)
    {
        var vex = await File.ReadAllTextAsync(Path.Combine(build, "pins.vex"), token);
        await File.WriteAllTextAsync(Path.Combine(build, FileName), Render(source, vex), token);
    }

    private static Ag32PinAssignment[] ReadPins(byte[] source)
    {
        var pins = new Ag32PinPlanDocument(source).Assignments;
        if (pins.Any(pin => !IsValid(pin)))
        {
            throw new StudioXException("AG32_GPIO_OPTIONS", "电气配置仅适用于 GPIO；输入模式不能设置开漏输出。");
        }
        return pins.Where(pin => IsGpio(pin.Function)).ToArray();
    }

    private static string Port(string vex, Ag32PinAssignment pin)
    {
        // -to 使用转换后的真实顶层端口，而不是将封装编号误作逻辑端口。
        var matches = Regex.Matches(vex, @"(?m)^[ \t]*([A-Za-z_][A-Za-z0-9_]*)[ \t]+PIN_" +
            pin.PinNumber.ToString(CultureInfo.InvariantCulture) + @"(?:[ \t\r]|$)");
        if (matches.Count != 1)
        {
            throw new StudioXException("AG32_GPIO_OPTIONS", $"PIN_{pin.PinNumber} 缺少唯一的电气配置目标。");
        }
        return matches[0].Groups[1].Value;
    }

    private static string Render(byte[] source, string vex)
    {
        var result = new StringBuilder("# StudioX GPIO electrical settings v1; generated from VE.\n");
        foreach (var pin in ReadPins(source))
        {
            var port = Port(vex, pin);
            var keep = pin.Pull switch
            {
                "UP" => "10",
                "DOWN" => "01",
                "HOLD" => "11",
                _ => "00"
            };
            result.AppendLine($"# {pin.Function} -> PIN_{pin.PinNumber}: {pin.Pull}, {pin.OutputType}");
            result.AppendLine($"set_instance_assignment -name CFG_KEEP -to {{{port}}} 2'b{keep} -extension");
            // 锁定版本的布局器在读入网表后不应用 AUTO_OPEN_DRAIN_PINS；使用其 IO 原语的明确参数并回查最终网表。
            result.AppendLine($"set_instance_assignment -name CFG_OPEN_DRAIN -to {{{port}}} 1'b{(pin.OutputType == "OPEN_DRAIN" ? 1 : 0)} -extension");
            if (pin.SlewControl)

            {
                result.AppendLine($"set_instance_assignment -name CFG_SLR -to {{{port}}} 1'b1 -extension");
            }
        }
        return result.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    internal static async Task VerifyAsync(string build, byte[] source, string vex, string routed, CancellationToken token)
    {
        if (!File.Exists(Path.Combine(build, FileName)) || await File.ReadAllTextAsync(Path.Combine(build, FileName), token) != Render(source, vex))
        {
            throw new StudioXException("AG32_GPIO_OPTIONS", "GPIO 电气约束缺失或与 VE 不一致，请重新编译。");
        }
        var cells = Regex.Matches(routed, @"\balta_rio\s+(?<name>\\\S+|[A-Za-z_][A-Za-z0-9_$]*)\s*\((?<body>.*?)\);", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        foreach (var pin in ReadPins(source))
        {
            var port = Port(vex, pin);
            var matched = cells.Where(cell => Regex.IsMatch(cell.Groups["body"].Value, @"\.padio\s*\(\s*" + Regex.Escape(port) + @"\s*\)")).ToArray();
            if (matched.Length != 1)
            {
                throw new StudioXException("AG32_GPIO_OPTIONS", $"缺少 {pin.Function} 的实际 IO 单元电气证据。");
            }
            var instance = Regex.Escape(matched[0].Groups["name"].Value);
            foreach (var (parameter, expected) in new[]
            {
                ("CFG_KEEP", pin.Pull switch { "UP" => 2, "DOWN" => 1, "HOLD" => 3, _ => 0 }),
                ("CFG_OPEN_DRAIN", pin.OutputType == "OPEN_DRAIN" ? 1 : 0),
                ("CFG_SLR", pin.SlewControl ? 1 : 0)
            })
            {
                var values = Regex.Matches(routed, @"\bdefparam\s+" + instance + @"\s*\." + parameter + @"\s*=\s*[12]'b([01]{1,2})\s*;", RegexOptions.CultureInvariant);
                if (parameter == "CFG_SLR" && expected == 0 && values.Count == 0)
                {
                    continue;
                }
                if (values.Count != 1 || Convert.ToInt32(values[0].Groups[1].Value, 2) != expected)
                {
                    throw new StudioXException("AG32_GPIO_OPTIONS", $"PIN_{pin.PinNumber} 的实际 {parameter} 与请求的 {Describe(pin)} 不一致，拒绝下载凭据。");
                }
            }
        }
    }
}
