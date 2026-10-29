namespace StudioX.StcConfigurator;

using System.Globalization;
using System.Text.Json.Nodes;
using StudioX.Extensions.Abstractions;

public sealed partial class StcConfigurationModel
{
    private static readonly string[] CounterPins = ["P3.4", "P3.5", "P3.1"];
    private static readonly string[] TimerOutputPins = ["P3.5", "P3.4", "P3.0"];
    private static readonly string[] InterruptPins = ["P3.2", "P3.3", "P3.6", "P3.7", "P3.0"];
    private static string[] UartPins(int index, string route) => index == 1
        ? route switch
        {
            "p36" => ["P3.6", "P3.7"],
            "p16" => ["P1.6", "P1.7"],
            _ => ["P3.0", "P3.1"]
        }
        : route == "p46" ? ["P4.6", "P4.7"] : ["P1.0", "P1.1"];
    private static string[] SpiPins(string route) => route switch { "p2" => ["P2.3", "P2.2", "P2.1"], "p4" => ["P4.0", "P4.1", "P4.3"], _ => ["P1.3", "P1.4", "P1.5"] };
    private static string[] PcaPins(string route) => route switch { "p3" => ["P3.4", "P3.5", "P3.6", "P3.7"], "p2" => ["P2.4", "P2.5", "P2.6", "P2.7"], _ => ["P1.2", "P1.1", "P1.0", "P3.7"] };
    private string Parameter(string id, string key, string fallback = "") => Peripheral(id)?["parameters"]?[key]?.ToString() ?? fallback;
    private static string Choice(string value, params string[] choices) => choices.Contains(value, StringComparer.Ordinal) ? value : throw new ArgumentException("选项无效：" + value);
    private static bool Flag(string value) => Choice(value, "0", "1") == "1";
    private static int Number(JsonNode node, string key) => node[key]!.GetValue<int>();

    private void ExtendedParameters(Func<string, string, string> value)
    {
        string V(string id, string fallback = "") => value(id, fallback);
        Configuration["profileData"]!["p54GpioConfirmed"] = Flag(V("p54Gpio", "0"));
        // 先建立整个候选资源集合，再统一换脚；同一次应用可以释放 A 并分配给 B。
        Peripherals.Clear();
        void Add(string id, JsonObject parameters) => Peripherals.Add(new JsonObject { ["peripheralId"] = id, ["enabled"] = true, ["parameters"] = parameters });
        for (var index = 1; index <= 2; index++)
        {
            var route = index == 1 ? Choice(V("uartRoute", "off"), "off", "default", "p36", "p16") : Choice(V("uart2Route", "off"), "off", "default", "p46");
            if (route == "off")
            {
                continue;
            }
            var source = index == 1 ? Choice(V("uartClock", "T2-1"), "T1-1", "T1-12", "T2-1", "T2-12") : "T2-" + Choice(V("uart2Divider", "1"), "1", "12");
            var parameters = new JsonObject { ["route"] = route, ["baudRate"] = Integer(V(index == 1 ? "baudRate" : "baudRate2", "9600"), 300, 1000000), ["clockSource"] = source[..2], ["divider"] = int.Parse(source[3..], CultureInfo.InvariantCulture) };
            // 缺省即关闭，旧工程无需改写不可变模板；只有用户选择后才记录生成选项。
            if (Flag(V("uart" + index + "Printf", "0")))
            {
                parameters["printfEnabled"] = true;
            }
            Add("UART" + index, parameters);
        }
        for (var index = 0; index < 3; index++)
        {
            var mode = Choice(V("timer" + index, "off"), "off", "1", "12", "external");
            if (mode == "off")
            {
                continue;
            }
            Add("TIMER" + index, new JsonObject { ["countMode"] = mode == "external" ? "external" : "internal", ["period"] = Integer(V("period" + index, "1000"), 1, mode == "external" ? 65536 : 1000000), ["divider"] = mode == "12" ? 12 : 1, ["clockOutput"] = Flag(V("timerOutput" + index, "0")) });
        }
        var speed = Choice(V("adcSpeed", "off"), "off", "540", "360", "180", "90");
        var mask = 0;
        for (var channel = 0; channel < 8; channel++)
        {
            if (Flag(V("adc" + channel, "0")))
            {
                mask |= 1 << channel;
            }
        }
        if (speed != "off")
        {
            Add("ADC", new JsonObject { ["channels"] = mask, ["cycles"] = int.Parse(speed, CultureInfo.InvariantCulture), ["rightAligned"] = Flag(V("adcAlignment", "1")) });
        }
        var spi = Choice(V("spiRoute", "off"), "off", "p1", "p2", "p4");
        if (spi != "off")
        {
            Add("SPI", new JsonObject { ["route"] = spi, ["mode"] = Integer(V("spiMode", "0"), 0, 3), ["divider"] = int.Parse(Choice(V("spiDivider", "4"), "4", "16", "64", "128"), CultureInfo.InvariantCulture), ["lsbFirst"] = Flag(V("spiOrder", "0")) });
        }
        var pca = Choice(V("pcaRoute", "off"), "off", "p1", "p3", "p2");
        if (pca != "off")
        {
            var parameters = new JsonObject { ["route"] = pca, ["clockSource"] = Choice(V("pcaClock", "12"), "1", "2", "4", "6", "8", "12", "T0", "ECI"), ["idleRun"] = Flag(V("pcaIdle", "1")) };
            for (var index = 0; index < 3; index++)
            {
                parameters["mode" + index] = Choice(V("pcaMode" + index, "off"), "off", "rising", "falling", "both", "compare", "toggle", "pwm8");
                parameters["value" + index] = Integer(V("pcaValue" + index, "128"), 0, 65535);
            }
            Add("PCA", parameters);
        }
        var exti = new JsonObject();
        for (var index = 0; index < 5; index++)
        {
            exti["int" + index] = index < 2 ? Choice(V("int" + index, "off"), "off", "falling", "both") : Choice(V("int" + index, "off"), "off", "falling");
        }
        if (exti.Any(item => item.Value!.GetValue<string>() != "off"))
        {
            Add("EXTI", exti);
        }
        var wdt = Choice(V("wdtDivider", "off"), "off", "2", "4", "8", "16", "32", "64", "128", "256");
        if (wdt != "off")
        {
            Add("WDT", new JsonObject { ["divider"] = int.Parse(wdt, CultureInfo.InvariantCulture), ["idleRun"] = Flag(V("wdtIdle", "0")) });
        }
        if (Flag(V("wakeEnabled", "0")))
        {
            Add("WKT", new JsonObject { ["counts"] = Integer(V("wakeCounts", "2048"), 1, 32768) });
        }
        if (Flag(V("lvdEnabled", "0")))
        {
            Add("LVD", new JsonObject { ["highPriority"] = Flag(V("lvdPriority", "0")) });
        }
        if (Flag(V("iapEnabled", "0")))
        {
            Add("IAP", new JsonObject());
        }
        var output = Choice(V("mclko", "off"), "off", "1", "2", "4");
        if (output != "off")
        {
            Add("MCLKO", new JsonObject { ["divider"] = int.Parse(output, CultureInfo.InvariantCulture) });
        }

        var desired = DesiredPeripheralPins();
        foreach (var pin in Pins.Where(pin => Text(pin!, "functionId") != "gpio" && (!desired.TryGetValue(PinName(pin!), out var function) || function != Text(pin!, "functionId"))).ToArray())
        {
            Pins.Remove(pin);
        }
        foreach (var (name, function) in desired)
        {
            var id = Text(Definitions.Single(pin => Text(pin!, "displayName") == name)!, "id");
            if (Pins.Any(pin => Text(pin!, "pinId") == id && Text(pin!, "functionId") == "gpio"))
            {
                throw new ArgumentException(name + " 已分配 GPIO，请先释放或修改外设引脚组。");
            }
            SetPin(id, function, null);
        }
        RebuildTimerClocks();
    }

    private Dictionary<string, string> DesiredPeripheralPins()
    {
        var desired = new Dictionary<string, string>(StringComparer.Ordinal);
        void Claim(string name, string function)
        {
            if (!desired.TryAdd(name, function))
            {
                throw new ArgumentException(name + " 同时被 " + desired[name] + " 和 " + function + " 占用，请调整引脚组。");
            }
        }
        for (var index = 1; index <= 2; index++)
        {
            if (Peripheral("UART" + index) is not null)
            {
                foreach (var name in UartPins(index, Parameter("UART" + index, "route")))
                {
                    Claim(name, "uart" + index);
                }
            }
        }
        for (var index = 0; index < 3; index++)
        {
            if (Peripheral("TIMER" + index) is not { } timer)
            {
                continue;
            }
            if (Text(timer["parameters"]!, "countMode") == "external")
            {
                Claim(CounterPins[index], "t" + index + "in");
            }
            if (timer["parameters"]!["clockOutput"]!.GetValue<bool>())
            {
                Claim(TimerOutputPins[index], "t" + index + "out");
            }
        }
        if (Peripheral("ADC") is { } adc)
        {
            for (var index = 0; index < 8; index++)
            {
                if ((Number(adc["parameters"]!, "channels") & (1 << index)) != 0)
                {
                    Claim("P1." + index, "adc");
                }
            }
        }
        if (Peripheral("SPI") is not null)
        {
            foreach (var name in SpiPins(Parameter("SPI", "route")))
            {
                Claim(name, "spi");
            }
        }
        if (Peripheral("PCA") is not null)
        {
            var names = PcaPins(Parameter("PCA", "route"));
            if (Parameter("PCA", "clockSource") == "ECI")
            {
                Claim(names[0], "eci");
            }
            for (var index = 0; index < 3; index++)
            {
                if (Parameter("PCA", "mode" + index) is not ("off" or "compare"))
                {
                    Claim(names[index + 1], "ccp" + index);
                }
            }
        }
        if (Peripheral("EXTI") is not null)
        {
            for (var index = 0; index < 5; index++)
            {
                if (Parameter("EXTI", "int" + index) != "off")
                {
                    Claim(InterruptPins[index], "int" + index);
                }
            }
        }
        if (Peripheral("MCLKO") is not null)
        {
            Claim("P5.4", "mclko");
        }
        return desired;
    }

    private void RebuildTimerClocks()
    {
        for (var index = 0; index < 3; index++)
        {
            var timer = Peripheral("TIMER" + index);
            var uart = Peripherals.FirstOrDefault(p => Text(p!, "peripheralId").StartsWith("UART", StringComparison.Ordinal) && Text(p!["parameters"]!, "clockSource") == "T" + index);
            var parameters = (timer ?? uart)?["parameters"];
            if (parameters is null)
            {
                continue;
            }
            Clocks.Add(new JsonObject { ["nodeId"] = "T" + index, ["enabled"] = true, ["inputId"] = timer is not null && Text(parameters, "countMode") == "external" ? "EXT" + index : "SYS", ["parameters"] = new JsonObject { ["divider"] = Number(parameters, "divider") } });
        }
    }

    private void ExtendedFields(List<PluginMcuField> fields)
    {
        PluginMcuOption O(string value, string label) => new(value, label);
        void S(string id, string section, string label, string value, params PluginMcuOption[] options) => fields.Add(new(id, section, label, "select", value, options));
        void N(string id, string section, string label, string value) => fields.Add(new(id, section, label, "text", value, []));
        void B(string id, string section, string label, bool value) => S(id, section, label, value ? "1" : "0", O("0", "否"), O("1", "是"));
        S("uartRoute", "通信 · UART1", "引脚组 · 8N1", Parameter("UART1", "route", "off"), O("off", "不启用"), O("default", "RX P3.0 / TX P3.1"), O("p36", "RX P3.6 / TX P3.7"), O("p16", "RX P1.6 / TX P1.7"));
        S("uartClock", "通信 · UART1", "波特率时钟 · 16 位自动重装", Parameter("UART1", "clockSource", "T2") + "-" + Parameter("UART1", "divider", "1"), O("T1-1", "Timer1 / 1T"), O("T1-12", "Timer1 / 12T"), O("T2-1", "Timer2 / 1T"), O("T2-12", "Timer2 / 12T"));
        N("baudRate", "通信 · UART1", "目标波特率 / baud", Parameter("UART1", "baudRate", "9600"));
        S("uart1Printf", "通信 · UART1", "生成 USART1_printf · 主循环轮询输出", PrintfEnabled(1) ? "1" : "0", O("0", "关闭"), O("1", "开启（需先启用 UART1）"));
        S("uart2Route", "通信 · UART2", "引脚组 · 8N1", Parameter("UART2", "route", "off"), O("off", "不启用"), O("default", "RX P1.0 / TX P1.1"), O("p46", "RX P4.6 / TX P4.7"));
        S("uart2Divider", "通信 · UART2", "波特率时钟（硬件固定 Timer2）", Parameter("UART2", "divider", "1"), O("1", "Timer2 / 1T"), O("12", "Timer2 / 12T"));
        N("baudRate2", "通信 · UART2", "目标波特率 / baud", Parameter("UART2", "baudRate", "9600"));
        S("uart2Printf", "通信 · UART2", "生成 USART2_printf · 主循环轮询输出", PrintfEnabled(2) ? "1" : "0", O("0", "关闭"), O("1", "开启（需先启用 UART2）"));
        for (var index = 0; index < 3; index++)
        {
            var id = "TIMER" + index;
            var section = "定时器 · Timer " + index;
            var mode = Peripheral(id) is null ? "off" : Parameter(id, "countMode") == "external" ? "external" : Parameter(id, "divider");
            S("timer" + index, section, "16 位自动重装 · 初始化后停止", mode, O("off", "不启用"), O("1", "定时 / SYSCLK / 1"), O("12", "定时 / SYSCLK / 12"), O("external", "外部脉冲 / " + CounterPins[index]));
            N("period" + index, section, mode == "external" ? "溢出所需脉冲数 / 1–65536" : "名义溢出周期 / µs", Parameter(id, "period", "1000"));
            B("timerOutput" + index, section, "时钟输出 / " + TimerOutputPins[index], Parameter(id, "clockOutput") == "true");
        }
        S("adcSpeed", "模拟 · ADC", "转换周期 / 内部 IRC（不分频）", Parameter("ADC", "cycles", "off"), O("off", "不启用"), O("540", "540 个 IRC 周期"), O("360", "360 个 IRC 周期"), O("180", "180 个 IRC 周期"), O("90", "90 个 IRC 周期"));
        S("adcAlignment", "模拟 · ADC", "10 位结果对齐", Parameter("ADC", "rightAligned", "true") == "true" ? "1" : "0", O("1", "右对齐"), O("0", "左对齐"));
        var mask = Peripheral("ADC")?["parameters"]?["channels"]?.GetValue<int>() ?? 0;
        for (var channel = 0; channel < 8; channel++)
        {
            B("adc" + channel, "模拟 · ADC", "通道 " + channel + " / P1." + channel, (mask & (1 << channel)) != 0);
        }
        S("spiRoute", "通信 · SPI 主机", "引脚组 · 片选另配 GPIO", Parameter("SPI", "route", "off"), O("off", "不启用"), O("p1", "MOSI P1.3 / MISO P1.4 / SCK P1.5"), O("p2", "MOSI P2.3 / MISO P2.2 / SCK P2.1"), O("p4", "MOSI P4.0 / MISO P4.1 / SCK P4.3"));
        S("spiMode", "通信 · SPI 主机", "时钟极性 / 相位", Parameter("SPI", "mode", "0"), O("0", "Mode 0 · CPOL=0 CPHA=0"), O("1", "Mode 1 · CPOL=0 CPHA=1"), O("2", "Mode 2 · CPOL=1 CPHA=0"), O("3", "Mode 3 · CPOL=1 CPHA=1"));
        S("spiDivider", "通信 · SPI 主机", "SCLK / SYSCLK 分频", Parameter("SPI", "divider", "4"), [.. new[] { 4, 16, 64, 128 }.Select(n => O(n.ToString(CultureInfo.InvariantCulture), "/ " + n))]);
        S("spiOrder", "通信 · SPI 主机", "位序", Parameter("SPI", "lsbFirst") == "true" ? "1" : "0", O("0", "MSB first"), O("1", "LSB first"));
        S("pcaRoute", "PCA · 三通道 CCP / PWM", "引脚组 · 初始化后停止", Parameter("PCA", "route", "off"), O("off", "不启用"), O("p1", "CCP0 P1.1 / CCP1 P1.0 / CCP2 P3.7"), O("p3", "CCP0 P3.5 / CCP1 P3.6 / CCP2 P3.7"), O("p2", "CCP0 P2.5 / CCP1 P2.6 / CCP2 P2.7"));
        S("pcaClock", "PCA · 三通道 CCP / PWM", "共用计数时钟", Parameter("PCA", "clockSource", "12"), [.. new[] { 1, 2, 4, 6, 8, 12 }.Select(n => O(n.ToString(CultureInfo.InvariantCulture), "SYSCLK / " + n)), O("T0", "Timer0 溢出"), O("ECI", "外部 ECI 下降沿")]);
        B("pcaIdle", "PCA · 三通道 CCP / PWM", "Idle 时继续计数", Parameter("PCA", "idleRun", "true") == "true");
        for (var index = 0; index < 3; index++)
        {
            var section = "PCA · CCP" + index;
            S("pcaMode" + index, section, "通道模式", Parameter("PCA", "mode" + index, "off"), O("off", "不启用"), O("rising", "上升沿捕获"), O("falling", "下降沿捕获"), O("both", "双边沿捕获"), O("compare", "16 位比较 / 软件定时"), O("toggle", "16 位比较 / 翻转输出"), O("pwm8", "8 位 PWM"));
            N("pcaValue" + index, section, Parameter("PCA", "mode" + index) == "pwm8" ? "高电平计数 / 0–256" : "比较值 / 0–65535（捕获时忽略）", Parameter("PCA", "value" + index, "128"));
        }
        for (var index = 0; index < 5; index++)
        {
            var options = new List<PluginMcuOption> { O("off", "不配置"), O("falling", "下降沿 · 中断保持关闭") };
            if (index < 2)
            {
                options.Add(O("both", "双边沿 · 中断保持关闭"));
            }
            S("int" + index, "系统 · 外部中断", "INT" + index + " / " + InterruptPins[index], Parameter("EXTI", "int" + index, "off"), [.. options]);
        }
        S("wdtDivider", "系统 · 看门狗", "立即启用 / SYSCLK÷12 预分频", Parameter("WDT", "divider", "off"), [O("off", "不配置"), .. new[] { 2, 4, 8, 16, 32, 64, 128, 256 }.Select(n => O(n.ToString(CultureInfo.InvariantCulture), "/ " + n))]);
        B("wdtIdle", "系统 · 看门狗", "Idle 时继续计数（业务须自行喂狗）", Parameter("WDT", "idleRun") == "true");
        B("wakeEnabled", "系统 · 掉电唤醒", "配置唤醒定时器（不自动进入掉电）", Peripheral("WKT") is not null);
        N("wakeCounts", "系统 · 掉电唤醒", "计数 / 1–32768，约 488 µs/次，需校准", Parameter("WKT", "counts", "2048"));
        B("lvdEnabled", "系统 · 低压检测", "初始化标志 / 保持中断关闭，门限由 ISP 设置", Peripheral("LVD") is not null);
        B("lvdPriority", "系统 · 低压检测", "高优先级", Parameter("LVD", "highPriority") == "true");
        B("iapEnabled", "存储 · IAP / EEPROM", "待机初始化 / 生成等待周期常量", Peripheral("IAP") is not null);
        B("p54Gpio", "系统 · P5.4 / 主时钟输出", "已在 ISP 关闭 P5.4 复位功能", Configuration["profileData"]!["p54GpioConfirmed"]!.GetValue<bool>());
        S("mclko", "系统 · P5.4 / 主时钟输出", "MCLKO · 内部 IRC，最高 8 MHz", Parameter("MCLKO", "divider", "off"), O("off", "不配置"), O("1", "IRC / 1"), O("2", "IRC / 2"), O("4", "IRC / 4"));
    }
}
