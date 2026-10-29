namespace StudioX.StcConfigurator;

using System.Globalization;
using System.Text;
using StudioX.Extensions.Abstractions;

public sealed partial class StcConfigurationModel
{
    public string GenerateCode() => RenderCode(false)[0].Content;

    public PluginMcuGeneratedFile[] GenerateFiles()
    {
        if (ClockHz is null)
        {
            throw new InvalidOperationException("请先在时钟树确认源频率与系统分频，再生成代码。");
        }
        var files = RenderCode(true).ToList();
        var header = files.FindIndex(file => file.Path == "include/generated/mcuconfig/mcuconfig.h");
        files[header] = files[header] with
        {
            Content = files[header].Content.Replace("#include <stdint.h>\n", "#include <stdint.h>\n#include \"generated/mcuconfig/Delay.h\"\n", StringComparison.Ordinal)
        };
        files.AddRange(RenderDelayFiles());
        return [.. files];
    }

    private PluginMcuGeneratedFile[] RenderCode(bool split)
    {
        var errors = Errors();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join("\n", errors));
        }
        if (extended)
        {
            return RenderPeripheralCode(split);
        }
        var output = new StringBuilder();
        var entry = output;
        var modules = new Dictionary<string, string>();
        void Line(string text = "") => output.Append(text).Append('\n');
        void BeginModule(string name)
        {
            if (!split)
            {
                return;
            }
            entry.Append("    sx_").Append(name).Append("_init();\n");
            output = new StringBuilder();
            Line("#include \"stc15.h\"");
            Line("#include \"generated/mcuconfig/" + name + ".h\"");
            Line("/* 由 sx_mcuconfig_init 在资源检查和中断保护后调用；应用从总入口初始化。 */");
            Line("void sx_" + name + "_init(void)");
            Line("{");
        }
        void EndModule(string name)
        {
            if (!split)
            {
                return;
            }
            Line("}");
            modules.Add(name, output.ToString());
            output = entry;
        }
        if (split)
        {
            Line("#include \"generated/mcuconfig/mcuconfig.h\"");
            foreach (var name in new[] { "gpio", "timer", "uart" })
            {
                Line("#include \"generated/mcuconfig/" + name + ".h\"");
            }
        }
        Line("#include \"stc15.h\"");
        if (!split)
        {
            output.Append(PinAliases());
        }
        Line("#if !defined(IAP15L2K61S2) || !defined(STUDIOX_STC15)");
        Line("#error Configuration_target_does_not_match_IAP15L2K61S2");
        Line("#endif");
        if (ClockHz is { } frequency)
        {
            Line($"/* 源 {SourceClockHz} Hz / {SystemDivider} = 系统 {frequency} Hz；ISP 配置源，配套启动文件配置分频。 */");
            Line($"#if defined(STUDIOX_CLOCK_HZ) && STUDIOX_CLOCK_HZ != {frequency}UL");
            Line("#error Clock_changed_regenerate_configuration");
            Line("#endif");
            Line($"#if defined(STUDIOX_CLOCK_DIVIDER) && STUDIOX_CLOCK_DIVIDER != {SystemDivider}");
            Line("#error Clock_divider_changed_regenerate_configuration");
            Line("#endif");
        }
        if (!Reserve && (Peripheral("UART1") is not null || Peripheral("TIMER0") is not null || Peripheral("TIMER1") is not null || Pins.Any(pin => PinName(pin!) is "P3.0" or "P3.1")))
        {
            Line("#if defined(STUDIOX_MON51_DEBUG)");
            Line("#error Configuration_uses_Mon51_resources");
            Line("#endif");
        }
        Line();
        Line("/* 上电初始化入口，用户明确调用。成功返回 1，资源运行中返回 0。");
        Line(" * GPIO 先写锁存值再改模式，保留未选引脚；不主动开启中断。");
        Line(" * Timer0/1 配置为 16 位自动重装但保持停止，应用自行启动和处理中断。");
        Line(" * UART1 使用 Timer2/1T；请在 UART 通信开始前调用，不会发送数据。");
        Line(" * 当前配置须在工程中编译并按实际连线验证；预览不代表实板验收。 */");
        Line("unsigned char sx_mcuconfig_init(void)");
        Line("{");
        Line("    unsigned char saved_ea = EA;");
        Line("    EA = 0;");
        var busy = new List<string>();
        for (var timer = 0; timer < 2; timer++)
        {
            if (Peripheral("TIMER" + timer) is not null)
            {
                busy.Add($"TR{timer} || ET{timer} || (INT_CLKO & 0x{1 << timer:X2}) || (TMOD & 0x03) == 0x03 || (TMOD & 0x30) == 0x30");
            }
        }
        if (Peripheral("UART1") is not null)
        {
            busy.Add("ES || (AUXR & 0x10) || (IE2 & 0x04) || (INT_CLKO & 0x04)");
        }
        if (busy.Count > 0)
        {
            Line("    if (" + string.Join(" || ", busy) + ")");
            Line("    {");
            Line("        EA = saved_ea;");
            Line("        return 0;");
            Line("    }");
        }
        BeginModule("gpio");
        for (var port = 0; port < 5; port++)
        {
            var gpio = Pins.Where(pin => Text(pin!, "functionId") == "gpio" && PinName(pin!).StartsWith("P" + port + ".", StringComparison.Ordinal)).ToArray();
            if (gpio.Length == 0)
            {
                continue;
            }
            var mask = 0;
            var high = 0;
            var m1 = 0;
            var m0 = 0;
            foreach (var pin in gpio)
            {
                var bit = 1 << (PinName(pin!)[3] - '0');
                mask |= bit;
                if (pin!["parameters"]!["initialLevel"]!.GetValue<int>() == 1)
                {
                    high |= bit;
                }
                var mode = Text(pin["parameters"]!, "mode");
                if (mode is "input" or "openDrain")
                {
                    m1 |= bit;
                }
                if (mode is "pushPull" or "openDrain")
                {
                    m0 |= bit;
                }
                Line("    /* " + PinName(pin) + " " + Text(pin, "label") + " */");
            }
            Line($"    P{port} = (P{port} & 0x{255 - mask:X2}) | 0x{high:X2};");
            if (port == 1)
            {
                Line($"    P1ASF &= 0x{255 - mask:X2};");
            }
            Line($"    P{port}M1 = (P{port}M1 & 0x{255 - mask:X2}) | 0x{m1:X2};");
            Line($"    P{port}M0 = (P{port}M0 & 0x{255 - mask:X2}) | 0x{m0:X2};");
        }
        EndModule("gpio");
        BeginModule("timer");
        for (var timer = 0; timer < 2; timer++)
        {
            if (Peripheral("TIMER" + timer) is not { } config)
            {
                continue;
            }
            var divisor = Clock("T" + timer)!["parameters"]!["divider"]!.GetValue<int>();
            var ticks = Ticks(config, ClockHz!.Value, divisor);
            var reload = (65536 - ticks) & 65535;
            var bit = timer == 0 ? 128 : 64;
            Line($"    /* Timer {timer}: {divisor}T, {ticks} ticks, reload 0x{reload:X4}. */");
            Line($"    TR{timer} = 0;");
            Line($"    AUXR = (AUXR & 0x{255 - bit:X2}) | 0x{(divisor == 1 ? bit : 0):X2};");
            Line($"    TMOD &= 0x{(timer == 0 ? 240 : 15):X2};");
            Line($"    TL{timer} = 0x{reload & 255:X2};");
            Line($"    TH{timer} = 0x{reload >> 8:X2};");
            Line($"    TF{timer} = 0;");
        }
        EndModule("timer");
        BeginModule("uart");
        if (Peripheral("UART1") is { } uart)
        {
            var baud = Baud(uart, ClockHz!.Value);
            var route = Text(uart["parameters"]!, "route");
            var port = route == "p16" ? 1 : 3;
            var rx = route == "default" ? 1 : 64;
            var tx = route == "default" ? 2 : 128;
            Line("    /* UART1 nominal " + baud.Rate.ToString("0.###", CultureInfo.InvariantCulture) + " baud, quantization error " + baud.ErrorPercent.ToString("0.###", CultureInfo.InvariantCulture) + "%. */");
            Line($"    P_SW1 = (P_SW1 & 0x3F) | 0x{(route == "p36" ? 64 : route == "p16" ? 128 : 0):X2};");
            Line($"    P{port} |= 0x{tx:X2};");
            if (port == 1)
            {
                Line("    P1ASF &= 0x3F;");
            }
            Line($"    P{port}M1 = (P{port}M1 & 0x{255 - tx:X2}) | 0x{rx:X2};");
            Line($"    P{port}M0 = (P{port}M0 & 0x{255 - rx:X2}) | 0x{tx:X2};");
            Line("    SCON = 0x50;");
            Line("    AUXR &= 0xEF;");
            Line("    AUXR = (AUXR & 0xF3) | 0x04;");
            Line($"    T2L = 0x{baud.Reload & 255:X2};");
            Line($"    T2H = 0x{baud.Reload >> 8:X2};");
            Line("    AUXR |= 0x11;");
        }
        EndModule("uart");
        Line("    EA = saved_ea;");
        Line("    return 1;");
        Line("}");
        var result = new List<PluginMcuGeneratedFile> { new("src/generated/mcuconfig/mcuconfig.c", output.ToString()) };
        if (split)
        {
            modules.Add("mcuconfig", "");
            foreach (var (name, source) in modules)
            {
                var guard = "STUDIOX_GENERATED_" + name.ToUpperInvariant() + "_H";
                var declaration = name == "mcuconfig" ? "uint8_t sx_mcuconfig_init(void);" : "void sx_" + name + "_init(void);";
                var aliases = name == "gpio" ? "#include \"stc15.h\"\n" + PinAliases() : name == "mcuconfig" ? "#include \"generated/mcuconfig/gpio.h\"\n" : "";
                result.Add(new("include/generated/mcuconfig/" + name + ".h", "#ifndef " + guard + "\n#define " + guard + "\n#include <stdint.h>\n" + aliases + declaration + "\n#endif\n"));
                if (name != "mcuconfig")
                {
                    result.Add(new("src/generated/mcuconfig/" + name + ".c", source));
                }
            }
        }
        return result.ToArray();
    }
}
