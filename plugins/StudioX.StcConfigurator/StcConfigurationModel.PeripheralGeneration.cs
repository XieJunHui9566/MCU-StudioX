namespace StudioX.StcConfigurator;

using System.Globalization;
using System.Text;
using StudioX.Extensions.Abstractions;

public sealed partial class StcConfigurationModel
{
    private PluginMcuGeneratedFile[] RenderPeripheralCode(bool split)
    {
        var frequency = ClockHz ?? 0;
        var modules = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var headerExtras = new Dictionary<string, string>(StringComparer.Ordinal);
        StringBuilder Module(string name)
        {
            var body = new StringBuilder();
            modules.Add(name, body);
            return body;
        }
        void L(StringBuilder body, string line) => body.Append("    ").Append(line).Append('\n');
        void Bits(StringBuilder body, string register, int mask, int value) => L(body, $"{register} = ({register} & 0x{255 - mask:X2}) | 0x{value:X2};");
        void Pin(StringBuilder body, string name, bool output, bool high = false, bool analog = false)
        {
            var port = "P" + name[1];
            var mask = 1 << (name[3] - '0');
            // 先置锁存器，再设模式；模拟输入不强驱动，所有掩码只影响所选引脚。
            Bits(body, port, mask, output && !high ? 0 : mask);
            Bits(body, port + "M1", mask, output ? 0 : mask);
            Bits(body, port + "M0", mask, output ? mask : 0);
            if (port == "P1")
            {
                Bits(body, "P1ASF", mask, analog ? mask : 0);
            }
        }
        void Timer(StringBuilder body, int index, int divider, int reload, bool external, bool clockOutput)
        {
            if (index < 2)
            {
                L(body, $"TR{index} = 0;");
                L(body, $"ET{index} = 0;");
                Bits(body, "AUXR", index == 0 ? 128 : 64, divider == 1 ? (index == 0 ? 128 : 64) : 0);
                Bits(body, "TMOD", index == 0 ? 15 : 240, external ? (index == 0 ? 4 : 64) : 0);
                L(body, $"TL{index} = 0x{reload & 255:X2};");
                L(body, $"TH{index} = 0x{reload >> 8:X2};");
                L(body, $"TF{index} = 0;");
            }
            else
            {
                L(body, "IE2 &= 0xFB; /* ET2=0，T2 的请求标志不可直接访问。 */");
                Bits(body, "AUXR", 0x1C, (divider == 1 ? 4 : 0) | (external ? 8 : 0));
                L(body, $"T2L = 0x{reload & 255:X2};");
                L(body, $"T2H = 0x{reload >> 8:X2};");
            }
            if (external)
            {
                Pin(body, CounterPins[index], false);
            }
            if (clockOutput)
            {
                Pin(body, TimerOutputPins[index], true);
            }
            Bits(body, "INT_CLKO", 1 << index, clockOutput ? 1 << index : 0);
        }

        var gpio = Module("gpio");
        for (var port = 0; port < 6; port++)
        {
            var selected = Pins.Where(p => Text(p!, "functionId") == "gpio" && PinName(p!)[1] - '0' == port).ToArray();
            if (selected.Length == 0)
            {
                continue;
            }
            int mask = 0, high = 0, m1 = 0, m0 = 0;
            foreach (var pin in selected)
            {
                var bit = 1 << (PinName(pin!)[3] - '0');
                mask |= bit;
                if (Number(pin!["parameters"]!, "initialLevel") == 1)
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
            }
            Bits(gpio, "P" + port, mask, high);
            if (port == 1)
            {
                Bits(gpio, "P1ASF", mask, 0);
            }
            Bits(gpio, "P" + port + "M1", mask, m1);
            Bits(gpio, "P" + port + "M0", mask, m0);
        }
        var timerBody = Module("timer");
        for (var index = 0; index < 3; index++)
        {
            if (Peripheral("TIMER" + index) is not { } timer)
            {
                continue;
            }
            var p = timer["parameters"]!;
            var reload = (int)((65536 - ExtendedTicks(timer, frequency)) & 65535);
            L(timerBody, $"/* Timer{index}：16 位自动重装；应用设置启动位，编写 ISR 后才启用中断。 */");
            Timer(timerBody, index, Number(p, "divider"), reload, Text(p, "countMode") == "external", p["clockOutput"]!.GetValue<bool>());
        }
        var uartBody = Module("uart");
        var initializedTimers = new HashSet<int>();
        for (var index = 1; index <= 2; index++)
        {
            if (Peripheral("UART" + index) is not { } uart)
            {
                continue;
            }
            var p = uart["parameters"]!;
            var timer = Text(p, "clockSource")[1] - '0';
            var baud = ExtendedBaud(uart, frequency);
            L(uartBody, "/* UART" + index + ": " + baud.Rate.ToString("0.###", CultureInfo.InvariantCulture) + " baud，量化误差 " + baud.ErrorPercent.ToString("0.###", CultureInfo.InvariantCulture) + "%。 */");
            if (initializedTimers.Add(timer))
            {
                Timer(uartBody, timer, Number(p, "divider"), baud.Reload, false, false);
            }
            var route = Text(p, "route");
            var names = UartPins(index, route);
            Pin(uartBody, names[0], false);
            Pin(uartBody, names[1], true, true);
            if (index == 1)
            {
                L(uartBody, "ES = 0;");
                Bits(uartBody, "P_SW1", 0xC0, route == "p36" ? 0x40 : route == "p16" ? 0x80 : 0);
                L(uartBody, "PCON &= 0xBF; /* SMOD0=0，SCON.7 用于模式选择。 */");
                L(uartBody, "CLK_DIV &= 0xEF; /* 关闭 UART1 转发；保留系统分频及 ADC 位。 */");
                L(uartBody, "SCON = 0x50; /* Mode 1 / 8N1，接收使能，不发送。 */");
                Bits(uartBody, "AUXR", 1, timer == 2 ? 1 : 0);
            }
            else
            {
                L(uartBody, "IE2 &= 0xFE; /* ES2=0 */");
                Bits(uartBody, "P_SW2", 1, route == "p46" ? 1 : 0);
                L(uartBody, "S2CON = 0x10; /* 8 位 UART2，固定由 Timer2 产生波特率。 */");
            }
        }
        foreach (var timer in initializedTimers.Order())
        {
            L(uartBody, timer == 1 ? "TR1 = 1; /* 只启动串口所需的波特率时钟。 */" : "AUXR |= 0x10; /* 启动 Timer2 波特率时钟。 */");
        }

        if (Peripheral("ADC") is { } adc)
        {
            var body = Module("adc");
            var p = adc["parameters"]!;
            var mask = Number(p, "channels");
            var first = Enumerable.Range(0, 8).First(i => (mask & (1 << i)) != 0);
            for (var index = 0; index < 8; index++)
            {
                if ((mask & (1 << index)) != 0)
                {
                    Pin(body, "P1." + index, false, analog: true);
                }
            }
            L(body, "EADC = 0;");
            Bits(body, "CLK_DIV", 0x20, p["rightAligned"]!.GetValue<bool>() ? 0x20 : 0);
            var speed = Number(p, "cycles") switch
            {
                540 => 0,
                360 => 0x20,
                180 => 0x40,
                _ => 0x60
            };
            L(body, $"ADC_CONTR = 0x{0x80 | speed | first:X2}; /* MOV 写入，供电打开，不启动转换。 */");
            L(body, "__asm\n        nop\n        nop\n        nop\n        nop\n    __endasm;");
            L(body, "/* 首次 ADC_START 前等待模拟电源稳定：建议预留 10 ms 并按电路验证；时钟为未分频内部 IRC。 */");
            headerExtras["adc"] = $"#define SX_ADC_CHANNEL_MASK 0x{mask:X2}\n#define SX_ADC_POWERUP_WAIT_US 10000UL\n";
        }
        if (Peripheral("SPI") is { } spi)
        {
            var body = Module("spi");
            var p = spi["parameters"]!;
            var route = Text(p, "route");
            var names = SpiPins(route);
            L(body, "IE2 &= 0xFD; /* ESPI=0 */");
            L(body, "SPCTL = 0; /* 停止接口后配置。 */");
            Pin(body, names[0], true);
            Pin(body, names[1], false);
            Pin(body, names[2], true, (Number(p, "mode") & 2) != 0);
            Bits(body, "P_SW1", 0x0C, route == "p2" ? 4 : route == "p4" ? 8 : 0);
            L(body, "SPSTAT = 0xC0; /* SPIF/WCOL 写 1 清除。 */");
            var divider = Number(p, "divider") switch
            {
                4 => 0,
                16 => 1,
                64 => 2,
                _ => 3
            };
            var control = 0xD0 | (p["lsbFirst"]!.GetValue<bool>() ? 0x20 : 0) | (Number(p, "mode") << 2) | divider;
            L(body, $"SPCTL = 0x{control:X2}; /* SSIG=1、主机；片选 GPIO 与 SPDAT 读写由业务管理。 */");
        }
        if (Peripheral("PCA") is { } pca)
        {
            var body = Module("pca");
            var p = pca["parameters"]!;
            var route = Text(p, "route");
            var names = PcaPins(route);
            L(body, "CCON = 0; /* CR=0，清除 CF/CCF0/1/2。 */");
            L(body, "CCAPM0 = 0; CCAPM1 = 0; CCAPM2 = 0;");
            L(body, "CL = 0; CH = 0;");
            var clock = Text(p, "clockSource") switch
            {
                "12" => 0,
                "2" => 1,
                "T0" => 2,
                "ECI" => 3,
                "1" => 4,
                "4" => 5,
                "6" => 6,
                _ => 7
            };
            L(body, $"CMOD = 0x{(clock << 1) | (p["idleRun"]!.GetValue<bool>() ? 0 : 128):X2}; /* ECF=0 */");
            Bits(body, "P_SW1", 0x30, route == "p3" ? 0x10 : route == "p2" ? 0x20 : 0);
            if (Text(p, "clockSource") == "ECI")
            {
                Pin(body, names[0], false);
            }
            for (var index = 0; index < 3; index++)
            {
                var mode = Text(p, "mode" + index);
                var value = Number(p, "value" + index);
                if (mode == "off")
                {
                    continue;
                }
                if (mode != "compare")
                {
                    Pin(body, names[index + 1], mode is "toggle" or "pwm8");
                }
                var compare = mode == "pwm8" ? 256 - value : mode is "rising" or "falling" or "both" ? 0 : value;
                L(body, $"PCA_PWM{index} = 0x{(mode == "pwm8" && compare == 256 ? 3 : 0):X2};");
                L(body, $"CCAP{index}L = 0x{compare & 255:X2};");
                L(body, $"CCAP{index}H = 0x{(mode == "pwm8" ? compare & 255 : compare >> 8):X2};");
                var modeBits = mode switch
                {
                    "rising" => 0x20,
                    "falling" => 0x10,
                    "both" => 0x30,
                    "compare" => 0x48,
                    "toggle" => 0x4C,
                    _ => 0x42
                };
                L(body, $"CCAPM{index} = 0x{modeBits:X2}; /* ECCF=0，业务按需处理捕获/比较标志。 */");
            }
            L(body, "/* 应用设置 CR=1 后开始；T0 溢出作为源时还需启动 TR0。 */");
        }
        if (Peripheral("EXTI") is not null)
        {
            var body = Module("exti");
            for (var index = 0; index < 5; index++)
            {
                var mode = Parameter("EXTI", "int" + index);
                if (mode == "off")
                {
                    continue;
                }
                Pin(body, InterruptPins[index], false);
                if (index < 2)
                {
                    L(body, $"EX{index} = 0; IE{index} = 0; IT{index} = {(mode == "falling" ? 1 : 0)};");
                }
                else
                {
                    Bits(body, "INT_CLKO", 1 << (index + 2), 0);
                }
            }
            L(body, "/* INT0/1：IT=0 是双边沿；INT2/3/4 固定下降沿。编写 ISR 后再开 EXn/EA。 */");
        }
        if (Peripheral("WKT") is { } wake)
        {
            var body = Module("wakeup");
            var value = Number(wake["parameters"]!, "counts") - 1;
            L(body, "WKTCH = 0; /* 配置时关闭计数器，不进入掉电。 */");
            L(body, $"WKTCL = 0x{value & 255:X2};");
            L(body, $"WKTCH = 0x{0x80 | (value >> 8):X2};");
            L(body, "/* 专用低速 RC /16；约 488 us/计数，存在温漂，精确定时需实测校准。 */");
        }
        if (Peripheral("LVD") is { } lvd)
        {
            var body = Module("lvd");
            L(body, "ELVD = 0;");
            L(body, "PCON &= 0xDF; /* 清 LVDF；门限来自 ISP，不由软件猜测。 */");
            Bits(body, "IP", 0x40, lvd["parameters"]!["highPriority"]!.GetValue<bool>() ? 0x40 : 0);
        }
        if (Peripheral("IAP") is not null)
        {
            var body = Module("iap");
            var wait = frequency <= 1000000 ? 7 : frequency <= 2000000 ? 6 : frequency <= 3000000 ? 5 : frequency <= 6000000 ? 4 : frequency <= 12000000 ? 3 : frequency <= 20000000 ? 2 : frequency <= 24000000 ? 1 : 0;
            L(body, "IAP_CONTR = 0; IAP_CMD = 0; IAP_TRIG = 0;");
            L(body, "IAP_ADDRH = 0xFF; IAP_ADDRL = 0xFF; /* 无效地址，保持 IAP 禁用。 */");
            L(body, "/* IAP15 可修改程序区；业务必须自行划定存储区域，本模块不触发读、写或擦除。 */");
            headerExtras["iap"] = $"#define SX_IAP_WAIT_BITS {wait}\n#define SX_IAP_SECTOR_BYTES 512U\n";
        }
        if (Peripheral("MCLKO") is { } mclko)
        {
            var body = Module("clock_output");
            Pin(body, "P5.4", true);
            Bits(body, "CLK_DIV", 0xC0, Number(mclko["parameters"]!, "divider") switch
            {
                1 => 0x40,
                2 => 0x80,
                _ => 0xC0
            });
        }
        if (Peripheral("WDT") is { } wdt)
        {
            // 最后开启看门狗，避免其它模块初始化消耗本次喂狗窗口。
            var body = Module("wdt");
            var p = wdt["parameters"]!;
            var prescaler = Array.IndexOf(new[] { 2, 4, 8, 16, 32, 64, 128, 256 }, Number(p, "divider"));
            L(body, $"WDT_CONTR = (WDT_CONTR & 0x80) | 0x{0x30 | prescaler | (p["idleRun"]!.GetValue<bool>() ? 8 : 0):X2};");
            L(body, "/* 已启用并首次清零；应用必须及时置 CLR_WDT 喂狗，不能依赖软件关闭。 */");
        }

        return PackagePeripheralCode(modules, headerExtras, split);
    }

    private PluginMcuGeneratedFile[] PackagePeripheralCode(Dictionary<string, StringBuilder> modules, Dictionary<string, string> extras, bool split)
    {
        var entry = new StringBuilder("#include \"stc15.h\"\n");
        var printf = RenderUartPrintf();
        if (printf.Source.Length > 0)
        {
            extras["uart"] = extras.GetValueOrDefault("uart", "") + printf.Header;
            if (!split)
            {
                // 格式化支持是函数定义，不能放入各模块的初始化语句中。
                entry.Append(printf.Source);
            }
        }
        if (split)
        {
            entry.Append("#include \"generated/mcuconfig/mcuconfig.h\"\n");
        }
        else
        {
            entry.Append(PinAliases());
        }
        foreach (var name in modules.Keys)
        {
            if (split)
            {
                entry.Append("#include \"generated/mcuconfig/").Append(name).Append(".h\"\n");
            }
        }
        entry.Append("#if !defined(IAP15L2K61S2) || !defined(STUDIOX_STC15)\n#error Configuration_target_mismatch\n#endif\n");
        if (ClockHz is { } frequency)
        {
            entry.Append($"#if defined(STUDIOX_CLOCK_HZ) && STUDIOX_CLOCK_HZ != {frequency}UL\n#error Clock_changed_regenerate_configuration\n#endif\n#if defined(STUDIOX_CLOCK_DIVIDER) && STUDIOX_CLOCK_DIVIDER != {SystemDivider}\n#error Clock_divider_changed_regenerate_configuration\n#endif\n");
        }
        if (!Reserve)
        {
            entry.Append("#if defined(STUDIOX_MON51_DEBUG)\n#error Configuration_uses_Mon51_resources\n#endif\n");
        }
        entry.Append("/* 上电初始化；业务负责启动普通计数器、读写和 ISR。所有本次配置的中断保持关闭。 */\nunsigned char sx_mcuconfig_init(void)\n{\n    unsigned char saved_ea = EA;\n    EA = 0;\n");
        var busy = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var used = Peripheral("TIMER" + index) is not null || Enumerable.Range(1, 2).Any(u => Parameter("UART" + u, "clockSource") == "T" + index);
            if (used)
            {
                busy.Add(index < 2 ? $"TR{index} || ET{index} || (TMOD & 0x03) == 0x03 || (TMOD & 0x30) == 0x30 || (INT_CLKO & 0x{1 << index:X2})" : "(AUXR & 0x10) || (IE2 & 0x04) || (INT_CLKO & 0x04)");
            }
        }
        if (Peripheral("UART1") is not null)
        {
            busy.Add("ES");
        }
        if (Peripheral("UART2") is not null)
        {
            busy.Add("(IE2 & 0x01)");
        }
        if (Peripheral("ADC") is not null)
        {
            busy.Add("EADC || (ADC_CONTR & 0x08)");
        }
        if (Peripheral("SPI") is not null)
        {
            busy.Add("(SPCTL & 0x40) || (IE2 & 0x02)");
        }
        if (Peripheral("PCA") is not null)
        {
            busy.Add("CR || (CMOD & 0x01) || (CCAPM0 & 0x01) || (CCAPM1 & 0x01) || (CCAPM2 & 0x01)");
        }
        if (Peripheral("IAP") is not null)
        {
            busy.Add("(IAP_CONTR & 0x80)");
        }
        if (busy.Count > 0)
        {
            entry.Append("    if (").Append(string.Join(" || ", busy)).Append(")\n    {\n        EA = saved_ea;\n        return 0;\n    }\n");
        }
        foreach (var (name, body) in modules)
        {
            entry.Append(split ? "    sx_" + name + "_init();\n" : body.ToString());
        }
        entry.Append("    EA = saved_ea;\n    return 1;\n}\n");
        var result = new List<PluginMcuGeneratedFile> { new("src/generated/mcuconfig/mcuconfig.c", entry.ToString()) };
        if (!split)
        {
            return [.. result];
        }
        foreach (var (name, body) in modules)
        {
            result.Add(new("src/generated/mcuconfig/" + name + ".c", "#include \"stc15.h\"\n#include \"generated/mcuconfig/" + name + ".h\"\n/* 从总入口调用，已完成资源检查和中断保护。 */\nvoid sx_" + name + "_init(void)\n{\n" + body + "}\n" + (name == "uart" ? printf.Source : "")));
            var guard = "STUDIOX_GENERATED_" + name.ToUpperInvariant() + "_H";
            result.Add(new("include/generated/mcuconfig/" + name + ".h", "#ifndef " + guard + "\n#define " + guard + "\n#include <stdint.h>\n" + (name == "gpio" ? "#include \"stc15.h\"\n" + PinAliases() : "") + extras.GetValueOrDefault(name, "") + "void sx_" + name + "_init(void);\n#endif\n"));
        }
        result.Add(new("include/generated/mcuconfig/mcuconfig.h", "#ifndef STUDIOX_GENERATED_MCUCONFIG_H\n#define STUDIOX_GENERATED_MCUCONFIG_H\n#include <stdint.h>\n#include \"generated/mcuconfig/gpio.h\"\n" + (printf.Source.Length > 0 ? "#include \"generated/mcuconfig/uart.h\"\n" : "") + "uint8_t sx_mcuconfig_init(void);\n#endif\n"));
        return [.. result];
    }
}
