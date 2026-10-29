namespace StudioX.StcConfigurator;

using System.Text.Json.Nodes;

public sealed partial class StcConfigurationModel
{
    private static void ExtendedShape(string id, JsonNode p)
    {
        void Range(string key, int minimum, int maximum)
        {
            if (Number(p, key) < minimum || Number(p, key) > maximum)
            {
                throw new ArgumentException(id + " 参数超出范围：" + key);
            }
        }
        void Divider(params int[] choices)
        {
            if (!choices.Contains(Number(p, "divider")))
            {
                throw new ArgumentException(id + " 分频无效。");
            }
        }
        switch (id)
        {
            case "GPIO0" or "GPIO1" or "GPIO2" or "GPIO3" or "GPIO4" or "GPIO5":
                Keys(p);
                break;
            case "UART1" or "UART2":
                if (p.AsObject().ContainsKey("printfEnabled"))
                {
                    Keys(p, "route", "baudRate", "clockSource", "divider", "printfEnabled");
                    if (p["printfEnabled"] is not JsonValue flag || !flag.TryGetValue<bool>(out _))
                    {
                        throw new ArgumentException(id + " printf 开关必须是布尔值。");
                    }
                }
                else
                {
                    Keys(p, "route", "baudRate", "clockSource", "divider");
                }
                _ = id == "UART1" ? Choice(Text(p, "route"), "default", "p36", "p16") : Choice(Text(p, "route"), "default", "p46");
                _ = id == "UART1" ? Choice(Text(p, "clockSource"), "T1", "T2") : Choice(Text(p, "clockSource"), "T2");
                Range("baudRate", 300, 1000000);
                Divider(1, 12);
                break;
            case "TIMER0" or "TIMER1" or "TIMER2":
                Keys(p, "countMode", "period", "divider", "clockOutput");
                Choice(Text(p, "countMode"), "internal", "external");
                Divider(1, 12);
                Range("period", 1, Text(p, "countMode") == "external" ? 65536 : 1000000);
                _ = p["clockOutput"]!.GetValue<bool>();
                break;
            case "ADC":
                Keys(p, "channels", "cycles", "rightAligned");
                Range("channels", 0, 255);
                if (Number(p, "cycles") is not (90 or 180 or 360 or 540))
                {
                    throw new ArgumentException("ADC 转换周期无效。");
                }
                _ = p["rightAligned"]!.GetValue<bool>();
                break;
            case "SPI":
                Keys(p, "route", "mode", "divider", "lsbFirst");
                Choice(Text(p, "route"), "p1", "p2", "p4");
                Range("mode", 0, 3);
                Divider(4, 16, 64, 128);
                _ = p["lsbFirst"]!.GetValue<bool>();
                break;
            case "PCA":
                Keys(p, "route", "clockSource", "idleRun", "mode0", "value0", "mode1", "value1", "mode2", "value2");
                Choice(Text(p, "route"), "p1", "p3", "p2");
                Choice(Text(p, "clockSource"), "1", "2", "4", "6", "8", "12", "T0", "ECI");
                _ = p["idleRun"]!.GetValue<bool>();
                for (var index = 0; index < 3; index++)
                {
                    Choice(Text(p, "mode" + index), "off", "rising", "falling", "both", "compare", "toggle", "pwm8");
                    Range("value" + index, 0, 65535);
                }
                break;
            case "EXTI":
                Keys(p, "int0", "int1", "int2", "int3", "int4");
                for (var index = 0; index < 5; index++)
                {
                    _ = index < 2 ? Choice(Text(p, "int" + index), "off", "falling", "both") : Choice(Text(p, "int" + index), "off", "falling");
                }
                break;
            case "WDT":
                Keys(p, "divider", "idleRun");
                Divider(2, 4, 8, 16, 32, 64, 128, 256);
                _ = p["idleRun"]!.GetValue<bool>();
                break;
            case "WKT":
                Keys(p, "counts");
                Range("counts", 1, 32768);
                break;
            case "LVD":
                Keys(p, "highPriority");
                _ = p["highPriority"]!.GetValue<bool>();
                break;
            case "IAP":
                Keys(p);
                break;
            case "MCLKO":
                Keys(p, "divider");
                Divider(1, 2, 4);
                break;
            default:
                throw new ArgumentException("准确型号未声明该外设：" + id);
        }
    }

    private void ExtendedErrors(List<string> errors)
    {
        var source = Text(Clock("SYS")!, "inputId");
        if (SourceClockHz is { } sourceHz && (sourceHz < 1000000 || source == "IRC" && sourceHz < 5000000 || sourceHz % SystemDivider != 0))
        {
            errors.Add("内部 IRC 需为 5–28 MHz，外部晶振为 1–28 MHz，系统分频需产生整数 Hz。");
        }
        if (ClockHz is null && (Peripherals.Any(p => !Text(p!, "peripheralId").StartsWith("GPIO", StringComparison.Ordinal)) || Configuration["profileData"]!["projectClock"]!["managed"]!.GetValue<bool>()))
        {
            errors.Add("请先在时钟树确认系统时钟来源和频率。");
        }
        if (Clocks.Any(c => c!["enabled"]!.GetValue<bool>() == false))
        {
            errors.Add("已声明的时钟节点必须启用；不使用的节点应移除。");
        }
        if (source == "XTAL" && Pins.Any(pin => PinName(pin!) is "P1.6" or "P1.7"))
        {
            errors.Add("外部晶振独占 P1.6/P1.7，与 GPIO、ADC 或 UART1 冲突。");
        }
        if (Pins.Any(pin => PinName(pin!) == "P5.4") && !Configuration["profileData"]!["p54GpioConfirmed"]!.GetValue<bool>())
        {
            errors.Add("使用 P5.4 前须确认 ISP 已关闭该引脚的复位功能。");
        }
        if (Reserve && (Pins.Any(pin => PinName(pin!) is "P3.0" or "P3.1") || Peripherals.Any(p => Text(p!, "peripheralId") is "UART1" or "UART2" or "TIMER0" or "TIMER1" or "TIMER2" or "PCA" or "IAP" or "WDT")))
        {
            errors.Add("监控保护：Mon51 的串口、定时器及运行资源未验收共存，普通应用请选择未使用监控。");
        }
        try
        {
            var desired = DesiredPeripheralPins();
            foreach (var (name, function) in desired)
            {
                if (!Pins.Any(p => PinName(p!) == name && Text(p!, "functionId") == function))
                {
                    errors.Add(name + " 缺少 " + function + " 分配；请应用对应外设参数。");
                }
            }
            foreach (var pin in Pins.Where(p => Text(p!, "functionId") != "gpio"))
            {
                if (!desired.TryGetValue(PinName(pin!), out var function) || function != Text(pin!, "functionId"))
                {
                    errors.Add(PinName(pin!) + " 的功能尚未在对应外设中配置，请设置完整参数。");
                }
            }
        }
        catch (ArgumentException error) { errors.Add(error.Message); }
        for (var index = 0; index < 3; index++)
        {
            var id = "T" + index;
            var timer = Peripheral("TIMER" + index);
            var owners = Peripherals.Where(p => Text(p!, "peripheralId").StartsWith("UART", StringComparison.Ordinal) && p!["enabled"]!.GetValue<bool>() && Text(p["parameters"]!, "clockSource") == id).ToArray();
            if (timer is not null && owners.Length > 0)
            {
                errors.Add("Timer " + index + " 已被串口波特率占用，不能同时用作独立定时器。");
            }
            if (owners.Length == 2 && (Number(owners[0]!["parameters"]!, "baudRate") != Number(owners[1]!["parameters"]!, "baudRate") || Number(owners[0]!["parameters"]!, "divider") != Number(owners[1]!["parameters"]!, "divider")))
            {
                errors.Add("UART1 与 UART2 共用 Timer2 时，波特率和 1T/12T 分频必须相同。");
            }
            var parameters = (timer ?? owners.FirstOrDefault())?["parameters"];
            if (parameters is not null)
            {
                var input = timer is not null && Text(parameters, "countMode") == "external" ? "EXT" + index : "SYS";
                if (Clock(id)?["parameters"]?["divider"]?.GetValue<int>() != Number(parameters, "divider") || Text(Clock(id) ?? new JsonObject(), "inputId") != input)
                {
                    errors.Add("Timer " + index + " 外设与时钟树配置不一致，请重新应用参数。");
                }
            }
            else if (Clock(id) is not null)
            {
                errors.Add("Timer " + index + " 时钟没有对应外设使用者。");
            }
            if (timer is not null && ClockHz is { } hz)
            {
                var ticks = ExtendedTicks(timer, hz);
                if (ticks is < 1 or > 65536)
                {
                    errors.Add("Timer " + index + " 周期超出 16 位范围。");
                }
                if (Text(timer["parameters"]!, "countMode") == "internal" && timer["parameters"]!["clockOutput"]!.GetValue<bool>() && ticks > 0 && hz / (2m * Number(timer["parameters"]!, "divider") * ticks) > 8000000m)
                {
                    errors.Add("Timer " + index + " 输出超过 3.3V 型号的 8 MHz 引脚限制。");
                }
            }
        }
        for (var index = 1; index <= 2; index++)
        {
            if (Peripheral("UART" + index) is { } uart && ClockHz is { } hz && ExtendedBaud(uart, hz).ErrorPercent > 2m)
            {
                errors.Add("UART" + index + " 波特率量化误差超过 2%，请调整波特率或时钟。");
            }
        }
        if (Peripheral("ADC") is { } adc && Number(adc["parameters"]!, "channels") == 0)
        {
            errors.Add("ADC 已启用，请至少选择一个模拟输入通道。");
        }
        if (Peripheral("PCA") is { } pca)
        {
            if (Parameter("PCA", "clockSource") == "T0" && Peripheral("TIMER0") is null)
            {
                errors.Add("PCA 使用 Timer0 溢出时钟，需要先配置 Timer0；运行时由应用启动 Timer0。");
            }
            for (var index = 0; index < 3; index++)
            {
                if (Parameter("PCA", "mode" + index) == "pwm8" && Number(pca["parameters"]!, "value" + index) > 256)
                {
                    errors.Add("CCP" + index + " 的 8 位 PWM 高电平计数应在 0–256。");
                }
            }
        }
        if (Peripheral("MCLKO") is { } mclk && (source != "IRC" || SourceClockHz is { } masterHz && masterHz / Number(mclk["parameters"]!, "divider") > 8000000))
        {
            errors.Add("该系列 MCLKO 按厂商 C 版限制仅开放内部 IRC，输出须不高于 8 MHz。");
        }
    }

    private static long ExtendedTicks(JsonNode timer, int frequency)
    {
        var p = timer["parameters"]!;
        return Text(p, "countMode") == "external" ? Number(p, "period") : (long)decimal.Round(frequency * (decimal)Number(p, "period") / (Number(p, "divider") * 1000000m), 0, MidpointRounding.AwayFromZero);
    }

    private static (int Reload, decimal Rate, decimal ErrorPercent) ExtendedBaud(JsonNode uart, int frequency)
    {
        var p = uart["parameters"]!;
        var baud = Number(p, "baudRate");
        var divisor = 4m * Number(p, "divider");
        var ideal = frequency / (divisor * baud);
        var choices = new[] { decimal.Floor(ideal), decimal.Ceiling(ideal) }.Where(n => n is >= 1 and <= 65536)
            .Select(n => (Reload: 65536 - (int)n, Rate: frequency / (divisor * n))).OrderBy(n => decimal.Abs(n.Rate - baud)).ToArray();
        return choices.Length == 0 ? (0, 0, 100m) : (choices[0].Reload, choices[0].Rate, decimal.Abs(choices[0].Rate - baud) * 100m / baud);
    }
}
