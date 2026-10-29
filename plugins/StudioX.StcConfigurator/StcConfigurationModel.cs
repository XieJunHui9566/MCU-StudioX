namespace StudioX.StcConfigurator;

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>准确型号的配置状态和规则；不依赖 StudioX 宿主实现或桌面框架。</summary>
public sealed partial class StcConfigurationModel
{
    private static readonly JsonObject Template = Asset("iap15l2k61s2-lqfp44.mcuconfig");
    private static readonly string DefinitionsHash = Asset("provenance.json")["definitionsSha256"]!.GetValue<string>();
    private static readonly string TemplateHash = Convert.ToHexStringLower(SHA256.HashData(AssetBytes("iap15l2k61s2-lqfp44.mcuconfig")));
    private static readonly JsonObject LegacyTemplate = Asset("legacy-v2.mcuconfig");
    private readonly bool extended;
    private string baseline;
    private string selectedPin = "pad-4";
    private string code = "";
    public JsonObject Document
    {
        get; private set;
    }
    public bool Dirty => Document["configuration"]!.ToJsonString() != baseline;
    private JsonObject Configuration => Document["configuration"]!.AsObject();
    private JsonArray Pins => Configuration["pins"]!.AsArray();
    private JsonArray Peripherals => Configuration["peripherals"]!.AsArray();
    private JsonArray Clocks => Configuration["clocks"]!.AsArray();
    private JsonArray Definitions => Document["definitions"]!["pins"]!.AsArray();

    public StcConfigurationModel(string? content = null)
    {
        Document = content is null ? (JsonObject)Template.DeepClone() : JsonNode.Parse(content, documentOptions: new JsonDocumentOptions { MaxDepth = 32 })?.AsObject()
            ?? throw new ArgumentException("配置不是 JSON 对象。");
        extended = Document["profile"]?["version"]?.GetValue<string>() == "3.0.0";
        var expectedTemplate = extended ? Template : LegacyTemplate;
        var expectedDefinitions = extended ? DefinitionsHash : Asset("legacy-v2-provenance.json")["definitionsSha256"]!.GetValue<string>();
        var expectedTemplateHash = extended ? TemplateHash : Convert.ToHexStringLower(SHA256.HashData(AssetBytes("legacy-v2.mcuconfig")));
        if (!JsonNode.DeepEquals(Document["target"], expectedTemplate["target"]) || !JsonNode.DeepEquals(Document["profile"], expectedTemplate["profile"]) ||
            !JsonNode.DeepEquals(Document["definitions"], expectedTemplate["definitions"]) || Document["formatVersion"]?.GetValue<int>() != 1 ||
            Document["format"]?.GetValue<string>() != "studiox.mcuconfig" ||
            content is not null && (Document["documentKind"]?.GetValue<string>() != "project" || Document["origin"]?["definitionsSha256"]?.GetValue<string>() != expectedDefinitions))
        {
            throw new ArgumentException("配置的格式、准确目标、profile、基础资料或绑定摘要不符。");
        }
        ValidateShape();
        if (content is not null)
        {
            Keys(Document["origin"], "packId", "packVersion", "packContentHash", "deviceId", "templateId", "templateFile", "templateSha256", "definitionsSha256");
            if (Document["origin"]!["templateFile"]!.GetValue<string>() != "templates/graphical/device.mcuconfig" ||
                Document["origin"]!["templateSha256"]!.GetValue<string>() != expectedTemplateHash)
            {
                throw new ArgumentException("初始模板来源摘要不符。");
            }
        }
        baseline = Configuration.ToJsonString();
    }

    private static JsonObject Asset(string suffix)
        => JsonNode.Parse(AssetBytes(suffix))!.AsObject();

    private static byte[] AssetBytes(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith(".Assets." + suffix, StringComparison.Ordinal)))!;
        using var result = new MemoryStream();
        stream.CopyTo(result);
        return result.ToArray();
    }

    public void MarkSaved() => baseline = Configuration.ToJsonString();

    public void Apply(JsonElement arguments)
    {
        var action = arguments.GetProperty("action").GetString();
        if (action == "selectPin")
        {
            var id = arguments.GetProperty("pinId").GetString()!;
            if (!Definitions.Any(pin => Text(pin!, "id") == id))
            {
                throw new ArgumentException("物理引脚不存在。");
            }
            selectedPin = id;
            return;
        }
        if (action == "generate")
        {
            var errors = Errors();
            if (errors.Count > 0)
            {
                code = "";
                throw new InvalidOperationException(string.Join("\n", errors));
            }
            code = GenerateCode();
            return;
        }
        if (action == "validate")
        {
            return;
        }
        var before = (JsonObject)Document.DeepClone();
        try
        {
            if (action == "assignPin")
            {
                var id = arguments.GetProperty("pinId").GetString()!;
                SetPin(id, arguments.GetProperty("functionId").GetString()!, null);
                selectedPin = id;
            }
            else if (action == "parameters")
            {
                Parameters(arguments.GetProperty("values"));
            }
            else if (action is "pinLabel" or "pinLock")
            {
                var pin = Pins.SingleOrDefault(item => Text(item!, "pinId") == arguments.GetProperty("pinId").GetString())
                    ?? throw new ArgumentException("请先给引脚分配功能。");
                if (action == "pinLabel")
                {
                    var label = arguments.GetProperty("label").GetString() ?? "";
                    ValidatePinLabel(label);
                    pin["label"] = label;
                }
                else if (arguments.GetProperty("locked").GetBoolean())
                {
                    pin["locked"] = true;
                }
                else
                {
                    pin.AsObject().Remove("locked");
                }
            }
            else
            {
                throw new ArgumentException("未知配置操作。");
            }
            SyncGpioRequests();
            // 所有配置入口都检查锁定，不允许外设换脚绕过封装菜单的保护。
            foreach (var locked in before["configuration"]!["pins"]!.AsArray().Where(pin => pin?["locked"]?.GetValue<bool>() == true))
            {
                var current = Pins.SingleOrDefault(pin => Text(pin!, "pinId") == Text(locked!, "pinId"));
                if (current is null || Text(current, "functionId") != Text(locked!, "functionId"))
                {
                    throw new ArgumentException(PinName(locked!) + " 已锁定，请先右键解除锁定。");
                }
            }
            ValidateShape();
            code = "";
        }
        catch { Document = before; throw; }
    }

    private void SyncGpioRequests()
    {
        foreach (var old in Peripherals.Where(item => Text(item!, "peripheralId").StartsWith("GPIO", StringComparison.Ordinal)).ToArray())
        {
            Peripherals.Remove(old);
        }
        foreach (var port in Pins.Where(item => Text(item!, "functionId") == "gpio").Select(item => "GPIO" + PinName(item!)[1]).Distinct(StringComparer.Ordinal))
        {
            Peripherals.Add(new JsonObject { ["peripheralId"] = port, ["enabled"] = true, ["parameters"] = new JsonObject() });
        }
    }

    private void SetPin(string id, string function, JsonObject? parameters)
    {
        var definition = Definitions.SingleOrDefault(pin => Text(pin!, "id") == id) ?? throw new ArgumentException("引脚不存在。");
        if (function.Length > 0 && !definition["functions"]!.AsArray().Any(item => Text(item!, "id") == function))
        {
            throw new ArgumentException("引脚不提供该功能。");
        }
        var old = Pins.FirstOrDefault(pin => Text(pin!, "pinId") == id);
        if (old?["locked"]?.GetValue<bool>() == true && Text(old, "functionId") != function)
        {
            throw new ArgumentException(PinName(old) + " 已锁定，请先右键解除锁定。");
        }
        if (old is not null && Text(old, "functionId") == function && parameters is null)
        {
            return;
        }
        if (old is not null)
        {
            Pins.Remove(old);
        }
        if (function.Length > 0)
        {
            Pins.Add(new JsonObject { ["pinId"] = id, ["functionId"] = function, ["label"] = old is null ? "" : Text(old, "label"), ["parameters"] = parameters ?? (function == "gpio" ? new JsonObject { ["mode"] = "quasi", ["initialLevel"] = 1 } : new JsonObject()) });
        }
    }

    private void Parameters(JsonElement values)
    {
        var known = new HashSet<string>(View("", true).Fields.Select(field => field.Id), StringComparer.Ordinal);
        known.Add("gpioLabel"); // 兼容已打开的旧表单；新界面由引脚右键菜单命名。
        if (values.EnumerateObject().Any(item => !known.Contains(item.Name)))
        {
            throw new ArgumentException("包含未知配置字段。");
        }
        var current = View("", true).Fields.ToDictionary(field => field.Id, field => field.Value, StringComparer.Ordinal);
        string Value(string id, string fallback = "") => values.TryGetProperty(id, out var value) ? value.GetString()! : current.GetValueOrDefault(id, fallback);
        var reserve = Value("reserve", "mon51");
        if (reserve is not ("mon51" or "none"))
        {
            throw new ArgumentException("监控保护选项无效。");
        }
        Configuration["profileData"]!["reserveMon51"] = reserve == "mon51";
        var source = Value("clockSource", "unknown");
        if (source is not ("unknown" or "IRC" or "XTAL"))
        {
            throw new ArgumentException("时钟来源无效。");
        }
        var frequencyText = Value("clockMHz").Trim();
        int? frequency = null;
        if (frequencyText.Length > 0)
        {
            if (!decimal.TryParse(frequencyText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mhz) ||
                mhz <= 0 || mhz > 28 || mhz * 1000000m != decimal.Truncate(mhz * 1000000m))
            {
                throw new ArgumentException("频率请输入 0–28 MHz 范围内的数值，最多 6 位小数；内部 IRC 还需满足 5–28 MHz。");
            }
            frequency = (int)(mhz * 1000000m);
        }
        var systemDivider = Integer(Value("systemDivider", "1"), 1, 128);
        if ((systemDivider & (systemDivider - 1)) != 0 || frequency is { } sourceHz && sourceHz % systemDivider != 0)
        {
            throw new ArgumentException("系统分频必须是 1–128 的 2 次幂，并产生整数 Hz 系统频率。");
        }
        if (source == "unknown" && frequency is not null)
        {
            throw new ArgumentException("请明确时钟来源，或清空尚未确认的频率。");
        }
        Clocks.Clear();
        Clocks.Add(new JsonObject { ["nodeId"] = "SYS", ["enabled"] = true, ["inputId"] = source == "unknown" ? null : source, ["parameters"] = new JsonObject { ["divider"] = systemDivider } });
        if (source != "unknown")
        {
            Clocks.Add(new JsonObject { ["nodeId"] = source, ["enabled"] = true, ["inputId"] = null, ["parameters"] = frequency is null ? new JsonObject() : new JsonObject { ["frequencyHz"] = frequency.Value } });
        }
        if (Pins.FirstOrDefault(pin => Text(pin!, "pinId") == selectedPin && Text(pin!, "functionId") == "gpio") is { } gpio)
        {
            var mode = Value("gpioMode", Text(gpio["parameters"]!, "mode"));
            if (mode is not ("quasi" or "pushPull" or "input" or "openDrain"))
            {
                throw new ArgumentException("GPIO 模式无效。");
            }
            gpio["parameters"]!["mode"] = mode;
            gpio["parameters"]!["initialLevel"] = Integer(Value("gpioLevel", "1"), 0, 1);
            if (values.TryGetProperty("gpioLabel", out var legacyLabel))
            {
                var label = legacyLabel.GetString() ?? "";
                ValidatePinLabel(label);
                gpio["label"] = label;
            }
        }
        if (extended)
        {
            ExtendedParameters(Value);
            return;
        }
        var uart = Value("uartRoute", "off");
        if (uart is not ("off" or "default" or "p36" or "p16"))
        {
            throw new ArgumentException("UART1 引脚组无效。");
        }
        var names = uart switch
        {
            "p36" => new[] { "P3.6", "P3.7" },
            "p16" => ["P1.6", "P1.7"],
            "default" => ["P3.0", "P3.1"],
            _ => []
        };
        var uartPins = names.Select(name => Text(Definitions.Single(pin => Text(pin!, "displayName") == name)!, "id")).ToArray();
        foreach (var pin in Pins.Where(pin => Text(pin!, "functionId") == "uart1" && !uartPins.Contains(Text(pin!, "pinId"), StringComparer.Ordinal)).ToArray())
        {
            Pins.Remove(pin);
        }
        if (Peripherals.FirstOrDefault(item => Text(item!, "peripheralId") == "UART1") is { } oldUart)
        {
            Peripherals.Remove(oldUart);
        }
        if (uart != "off")
        {
            foreach (var name in names)
            {
                var id = Text(Definitions.Single(pin => Text(pin!, "displayName") == name)!, "id");
                if (Pins.Any(pin => Text(pin!, "pinId") == id && Text(pin!, "functionId") != "uart1"))
                {
                    throw new ArgumentException(name + " 已分配 GPIO，请先释放；不会自动覆盖。");
                }
                SetPin(id, "uart1", null);
            }
            Peripherals.Add(new JsonObject { ["peripheralId"] = "UART1", ["enabled"] = true, ["parameters"] = new JsonObject { ["route"] = uart, ["baudRate"] = Integer(Value("baudRate", "9600"), 300, 1000000) } });
            Clocks.Add(new JsonObject { ["nodeId"] = "T2", ["enabled"] = true, ["inputId"] = "SYS", ["parameters"] = new JsonObject { ["divider"] = 1 } });
        }
        for (var timer = 0; timer < 2; timer++)
        {
            var timerId = "TIMER" + timer;
            if (Peripherals.FirstOrDefault(item => Text(item!, "peripheralId") == timerId) is { } old)
            {
                Peripherals.Remove(old);
            }
            var divisor = Value("timer" + timer, "off");
            if (divisor == "off")
            {
                continue;
            }
            var divider = Integer(divisor, 1, 12);
            if (divider is not (1 or 12))
            {
                throw new ArgumentException("定时器仅支持 1T 或 12T。");
            }
            Peripherals.Add(new JsonObject { ["peripheralId"] = timerId, ["enabled"] = true, ["parameters"] = new JsonObject { ["periodMicroseconds"] = Integer(Value("period" + timer, "1000"), 1, 1000000) } });
            Clocks.Add(new JsonObject { ["nodeId"] = "T" + timer, ["enabled"] = true, ["inputId"] = "SYS", ["parameters"] = new JsonObject { ["divider"] = divider } });
        }
    }

    private static int Integer(string text, int minimum, int maximum) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum
        ? value : throw new ArgumentException($"参数必须为 {minimum}–{maximum} 的十进制整数。");
    private static string Text(JsonNode value, string key) => value[key]?.GetValue<string>() ?? "";
    private JsonNode? Peripheral(string id) => Peripherals.FirstOrDefault(item => Text(item!, "peripheralId") == id && item!["enabled"]!.GetValue<bool>());
    private JsonNode? Clock(string id) => Clocks.FirstOrDefault(item => Text(item!, "nodeId") == id);
    private int SystemDivider => Clock("SYS")?["parameters"]?["divider"]?.GetValue<int>() ?? 1;
    private int? SourceClockHz => Clock(Text(Clock("SYS")!, "inputId"))?["parameters"]?["frequencyHz"]?.GetValue<int>();
    private int? ClockHz => SourceClockHz is { } source ? source / SystemDivider : null;
    private bool Reserve => Configuration["profileData"]!["reserveMon51"]!.GetValue<bool>();
}
