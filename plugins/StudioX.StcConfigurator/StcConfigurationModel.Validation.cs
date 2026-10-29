namespace StudioX.StcConfigurator;

using System.Globalization;
using System.Text.Json.Nodes;
using StudioX.Extensions.Abstractions;

public sealed partial class StcConfigurationModel
{
    private static void Keys(JsonNode? node, params string[] names)
    {
        if (node is not JsonObject value || value.Count != names.Length || names.Any(name => !value.ContainsKey(name)))
        {
            throw new ArgumentException("配置含缺失或未知字段。");
        }
    }

    private void ValidateShape()
    {
        Keys(Document, "format", "formatVersion", "documentKind", "target", "profile", "origin", "definitions", "configuration", "generation");
        Keys(Configuration, "pins", "peripherals", "clocks", "profileData");
        if (extended)
        {
            Keys(Configuration["profileData"], "reserveMon51", "projectClock", "p54GpioConfirmed");
            _ = Configuration["profileData"]!["p54GpioConfirmed"]!.GetValue<bool>();
        }
        else
        {
            Keys(Configuration["profileData"], "reserveMon51", "projectClock");
        }
        Keys(Configuration["profileData"]!["projectClock"], "managed", "valid", "inputSha256");
        _ = Configuration["profileData"]!["projectClock"]!["managed"]!.GetValue<bool>();
        _ = Configuration["profileData"]!["projectClock"]!["valid"]!.GetValue<bool>();
        _ = Reserve;
        if (Pins.Count > 44 || Peripherals.Count > (extended ? 21 : 8) || Clocks.Count > 6)
        {
            throw new ArgumentException("配置实例数量超限。");
        }
        Unique(Pins, "pinId");
        Unique(Peripherals, "peripheralId");
        Unique(Clocks, "nodeId");
        foreach (var pin in Pins)
        {
            if (pin?["locked"] is { } locked)
            {
                Keys(pin, "pinId", "functionId", "label", "parameters", "locked");
                _ = locked.GetValue<bool>();
            }
            else
            {
                Keys(pin, "pinId", "functionId", "label", "parameters");
            }
            var definition = Definitions.SingleOrDefault(item => Text(item!, "id") == Text(pin!, "pinId"));
            if (definition is null || !definition["functions"]!.AsArray().Any(item => Text(item!, "id") == Text(pin!, "functionId")))
            {
                throw new ArgumentException("配置引用了不存在或未支持的引脚功能。");
            }
            // 旧版名称仅用于显示；保留可读取性，由语义检查提示修正后再生成宏。
            var label = Text(pin!, "label");
            if (label.Length > 32 || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            {
                throw new ArgumentException("引脚名称仅接受 32 字符内的字母、数字和下划线。");
            }
            if (Text(pin!, "functionId") == "gpio")
            {
                Keys(pin!["parameters"], "mode", "initialLevel");
                if (Text(pin["parameters"]!, "mode") is not ("quasi" or "pushPull" or "input" or "openDrain") || pin["parameters"]!["initialLevel"]!.GetValue<int>() is not (0 or 1))
                {
                    throw new ArgumentException("GPIO 参数无效。");
                }
            }
            else
            {
                Keys(pin!["parameters"]);
            }
        }
        foreach (var peripheral in Peripherals)
        {
            Keys(peripheral, "peripheralId", "enabled", "parameters");
            _ = peripheral!["enabled"]!.GetValue<bool>();
            var id = Text(peripheral, "peripheralId");
            var parameters = peripheral["parameters"]!;
            if (extended)
            {
                ExtendedShape(id, parameters);
                continue;
            }
            if (id == "UART1")
            {
                Keys(parameters, "route", "baudRate");
                if (Text(parameters, "route") is not ("default" or "p36" or "p16") || parameters["baudRate"]!.GetValue<int>() is < 300 or > 1000000)
                {
                    throw new ArgumentException("UART1 参数无效。");
                }
            }
            else if (id is "TIMER0" or "TIMER1")
            {
                Keys(parameters, "periodMicroseconds");
                if (parameters["periodMicroseconds"]!.GetValue<int>() is < 1 or > 1000000)
                {
                    throw new ArgumentException("定时器周期无效。");
                }
            }
            else if (id is "GPIO0" or "GPIO1" or "GPIO2" or "GPIO3" or "GPIO4")
            {
                Keys(parameters);
            }
            else
            {
                throw new ArgumentException("外设不在首版支持范围内。");
            }
        }
        foreach (var clock in Clocks)
        {
            Keys(clock, "nodeId", "enabled", "inputId", "parameters");
            _ = clock!["enabled"]!.GetValue<bool>();
            var id = Text(clock, "nodeId");
            var input = Text(clock, "inputId");
            if (id is "IRC" or "XTAL")
            {
                if (clock["parameters"] is not JsonObject sourceParameters || sourceParameters.Any(item => item.Key != "frequencyHz") ||
                    sourceParameters["frequencyHz"] is { } sourceHz && sourceHz.GetValue<int>() is < 1 or > 28000000)
                {
                    throw new ArgumentException("时钟源频率无效。");
                }
                if (input.Length > 0)
                {
                    throw new ArgumentException("时钟源参数无效。");
                }
            }
            else if (id == "SYS")
            {
                if (clock["parameters"] is not JsonObject parameters || parameters.Count != 1 ||
                    parameters["divider"]?.GetValue<int>() is not (1 or 2 or 4 or 8 or 16 or 32 or 64 or 128))
                {
                    throw new ArgumentException("系统时钟频率无效。");
                }
                if (input is not ("" or "IRC" or "XTAL"))
                {
                    throw new ArgumentException("系统时钟源无效。");
                }
            }
            else if (id is "T0" or "T1" or "T2")
            {
                Keys(clock["parameters"], "divider");
                if (input != "SYS" && (!extended || input != "EXT" + id[1]) || clock["parameters"]!["divider"]!.GetValue<int>() is not (1 or 12))
                {
                    throw new ArgumentException("定时器时钟参数无效。");
                }
            }
            else
            {
                throw new ArgumentException("时钟节点未被支持。");
            }
        }
        if (Clock("SYS") is null)
        {
            throw new ArgumentException("系统时钟配置节点缺失。");
        }
    }

    private static void Unique(JsonArray array, string key)
    {
        if (array.Select(item => Text(item!, key)).Distinct(StringComparer.Ordinal).Count() != array.Count)
        {
            throw new ArgumentException("配置资源 ID 重复。");
        }
    }

    public IReadOnlyList<string> Errors()
    {
        var errors = new List<string>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in Pins)
        {
            var label = Text(pin!, "label");
            if (PinLabelError(label) is { } error)
            {
                errors.Add(PinName(pin!) + "：" + error);
            }
            if (label.Length > 0 && !labels.Add(label))
            {
                errors.Add("引脚名称“" + label + "”重复，每个宏名称只能对应一个引脚。");
            }
        }
        if (extended)
        {
            ExtendedErrors(errors);
            return errors;
        }
        var source = Text(Clock("SYS")!, "inputId");
        var hz = ClockHz;
        if (source == "XTAL" && Pins.Any(pin => PinName(pin!) is "P1.6" or "P1.7"))
        {
            errors.Add("外部晶振使用 P1.6/P1.7，不能同时分配 GPIO 或 UART1。");
        }
        if (Reserve && Pins.Any(pin => PinName(pin!) is "P3.0" or "P3.1"))
        {
            errors.Add("已选择保留 Mon51，P3.0/P3.1 不可分配。");
        }
        var uart = Peripheral("UART1");
        if (uart is not null && Reserve)
        {
            errors.Add("已选择保留 Mon51：监控使用 UART1，切换引脚也不能消除外设占用。");
        }
        // 首版没有验收监控与计时外设的并行运行，保护选项采用保守策略。
        if (Reserve && (Peripheral("TIMER0") is not null || Peripheral("TIMER1") is not null))
        {
            errors.Add("监控保护模式暂不生成 Timer0/1 初始化；普通应用请明确选择未使用监控。");
        }
        var uartPins = Pins.Where(pin => Text(pin!, "functionId") == "uart1").ToArray();
        if (uart is null && uartPins.Length > 0)
        {
            errors.Add("请在外设页明确启用 UART1 并选择完整 RX/TX 引脚组。");
        }
        if (uart is not null)
        {
            var names = Text(uart["parameters"]!, "route") switch
            {
                "p36" => new[] { "P3.6", "P3.7" },
                "p16" => ["P1.6", "P1.7"],
                _ => ["P3.0", "P3.1"]
            };
            if (uartPins.Length != 2 || names.Any(name => !uartPins.Any(pin => PinName(pin!) == name)))
            {
                errors.Add("UART1 配置与完整 RX/TX 引脚组不一致。");
            }
            if (Clock("T2")?["parameters"]?["divider"]?.GetValue<int>() != 1)
            {
                errors.Add("UART1 首版固定使用 Timer2 / 1T。");
            }
        }
        var timing = uart is not null || Peripheral("TIMER0") is not null || Peripheral("TIMER1") is not null;
        if (SourceClockHz is { } sourceFrequency && (sourceFrequency % SystemDivider != 0 || sourceFrequency < 1000000 || source == "IRC" && sourceFrequency < 5000000))
        {
            errors.Add("内部 RC 目标需为 5–28 MHz，系统分频必须产生整数 Hz；外部晶振按实装值填写。");
        }
        if ((timing || Configuration["profileData"]!["projectClock"]!["managed"]!.GetValue<bool>()) && (hz is null || source.Length == 0))
        {
            errors.Add("定时器/串口需要明确的系统时钟来源与整数 Hz 频率；不会猜测默认晶振。");
        }
        if (hz is { } frequency)
        {
            if (uart is not null && Baud(uart, frequency).ErrorPercent > 2m)
            {
                errors.Add("UART1 名义波特率量化误差超过 2%，请调整频率或波特率。");
            }
            for (var index = 0; index < 2; index++)
            {
                if (Peripheral("TIMER" + index) is not { } timer)
                {
                    continue;
                }
                var divider = Clock("T" + index)?["parameters"]?["divider"]?.GetValue<int>();
                if (divider is null)
                {
                    errors.Add("Timer " + index + " 时钟分频未设置。");
                    continue;
                }
                if (Ticks(timer, frequency, divider.Value) is < 1 or > 65536)
                {
                    errors.Add("Timer " + index + " 周期超出 16 位计数范围。");
                }
            }
        }
        return errors;
    }

    private string PinName(JsonNode selection) => Text(Definitions.Single(item => Text(item!, "id") == Text(selection, "pinId"))!, "displayName");
    private static long Ticks(JsonNode timer, int frequency, int divider) => (long)decimal.Round((decimal)frequency * timer["parameters"]!["periodMicroseconds"]!.GetValue<int>() / (divider * 1000000m), 0, MidpointRounding.AwayFromZero);
    private static (int Reload, decimal Rate, decimal ErrorPercent) Baud(JsonNode uart, int frequency)
    {
        var baud = uart["parameters"]!["baudRate"]!.GetValue<int>();
        var ideal = frequency / (4m * baud);
        var choices = new[] { decimal.Floor(ideal), decimal.Ceiling(ideal) }.Where(ticks => ticks is >= 1 and <= 65536)
            .Select(ticks => (Reload: 65536 - (int)ticks, Rate: frequency / (4m * ticks)))
            .OrderBy(choice => decimal.Abs(choice.Rate - baud)).ToArray();
        return choices.Length == 0 ? (0, 0, 100m) : (choices[0].Reload, choices[0].Rate, decimal.Abs(choices[0].Rate - baud) * 100m / baud);
    }

    public PluginMcuConfigurationState View(string status, bool canSave)
    {
        PluginMcuOption Option(string value, string label) => new(value, label);
        PluginMcuField Select(string id, string section, string label, string value, params PluginMcuOption[] options) => new(id, section, label, "select", value, options);
        PluginMcuField Input(string id, string section, string label, string value) => new(id, section, label, "text", value, []);
        var hz = ClockHz;
        var source = Text(Clock("SYS")!, "inputId");
        var fields = new List<PluginMcuField>
        {
            Select("clockSource", "时钟托管", "下载后使用的硬件时钟源", source.Length == 0 ? "unknown" : source, Option("unknown", "尚未确认"), Option("IRC", "内部 RC · ISP 校准"), Option("XTAL", "外部晶振 · 必须实装")),
            new("clockMHz", "时钟托管", "源时钟频率 / MHz（分频前）", "editableSelect",
                SourceClockHz is { } sourceHz ? (sourceHz / 1000000m).ToString("0.######", CultureInfo.InvariantCulture) : "",
                Template["definitions"]!["profileData"]!["ircFrequencyPresetsHz"]!.AsArray().Select(value =>
                { var mhz = (value!.GetValue<int>() / 1000000m).ToString("0.######", CultureInfo.InvariantCulture); return Option(mhz, mhz + " MHz"); }).ToArray()),
            Select("systemDivider", "时钟托管", "系统时钟分频", SystemDivider.ToString(CultureInfo.InvariantCulture), new[] { 1, 2, 4, 8, 16, 32, 64, 128 }.Select(value => Option(value.ToString(CultureInfo.InvariantCulture), "/ " + value)).ToArray()),
            Select("reserve", "资源保护", "保留 Mon51 串口资源（保护选项）", Reserve ? "mon51" : "none", Option("mon51", "监控保护：不初始化串口和定时器"), Option("none", "普通应用，未使用监控"))
        };
        if (Pins.FirstOrDefault(pin => Text(pin!, "pinId") == selectedPin && Text(pin!, "functionId") == "gpio") is { } selected)
        {
            fields.Add(Select("gpioMode", "当前 GPIO · " + PinName(selected), "电气模式", Text(selected["parameters"]!, "mode"), Option("quasi", "准双向 / 弱上拉"), Option("pushPull", "推挽输出"), Option("input", "高阻输入"), Option("openDrain", "开漏")));
            fields.Add(Select("gpioLevel", "当前 GPIO · " + PinName(selected), "初始锁存电平", selected["parameters"]!["initialLevel"]!.ToString(), Option("0", "低电平 0"), Option("1", "高电平 1")));
        }
        if (extended)
        {
            ExtendedFields(fields);
        }
        else
        {
            var uart = Peripheral("UART1");
            fields.Add(Select("uartRoute", "UART1 · 8N1 / Timer2 1T", "串口引脚组", uart is null ? "off" : Text(uart["parameters"]!, "route"), Option("off", "不启用"), Option("default", "RX P3.0 / TX P3.1"), Option("p36", "RX P3.6 / TX P3.7"), Option("p16", "RX P1.6 / TX P1.7")));
            fields.Add(Input("baudRate", "UART1 · 8N1 / Timer2 1T", "目标波特率 / baud", uart?["parameters"]?["baudRate"]?.ToString() ?? "9600"));
            for (var timer = 0; timer < 2; timer++)
            {
                var config = Peripheral("TIMER" + timer);
                fields.Add(Select("timer" + timer, "Timer " + timer + " · 16 位自动重装 / 不自动启动", "计数时钟", config is null ? "off" : Clock("T" + timer)!["parameters"]!["divider"]!.ToString(), Option("off", "不启用"), Option("1", "1T · SYSCLK"), Option("12", "12T · SYSCLK / 12")));
                fields.Add(Input("period" + timer, "Timer " + timer + " · 16 位自动重装 / 不自动启动", "名义周期 / µs", config?["parameters"]?["periodMicroseconds"]?.ToString() ?? "1000"));
            }
        }
        var pins = Definitions.Select(node =>
        {
            var pin = node!;
            var selected = Pins.FirstOrDefault(item => Text(item!, "pinId") == Text(pin, "id"));
            return new PluginMcuPin(Text(pin, "id"), Text(pin, "position"), Text(pin, "displayName"), Text(pin, "category"),
                pin["point"]!["x"]!.GetValue<double>(), pin["point"]!["y"]!.GetValue<double>(), Text(pin, "side"),
                pin["functions"]!.AsArray().Select(function => Option(Text(function!, "id"), Text(function!, "displayName"))).ToArray(), selected is null ? "" : Text(selected, "functionId"), selected is null ? "" : Text(selected, "label"))
            {
                Locked = selected?["locked"]?.GetValue<bool>() == true,
                CanEditMetadata = selected is not null
            };
        }).ToArray();
        var clocks = ClockView();
        var errors = Errors();
        return new(1, "IAP15L2K61S2", extended ? "LQFP44 · T0/1/2 · UART1/2 · ADC · SPI · PCA · 系统外设" : "LQFP44 · 旧模板 · GPIO / UART1 / Timer0 / Timer1", "LQFP44 · 44 引脚", 1000, 1000, pins, clocks,
            selectedPin, fields.Select(field => field.Id is "clockSource" or "clockMHz" or "systemDivider" ? field with
            {
                Page = "clock",
                ClockNodeId = field.Id == "clockSource" ? "SOURCE_MUX" : field.Id == "systemDivider" ? "SYS" : source == "XTAL" ? "XTAL" : "IRC"
            } : field).ToArray(), [.. errors], code, status, Dirty, canSave, errors.Count == 0 && hz is not null);
    }
}
