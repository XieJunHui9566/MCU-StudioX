namespace StudioX.Engine;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>经哈希记录的 AGM 原厂完整驱动与模拟 IP，同时用于旧工程补齐和新工程生成。</summary>
public static class Ag32PeripheralSupport
{
    public const string Marker = "#@StudioX:ANALOG";
    internal const string Prefix = "StudioX.Ag32.Peripherals.";
    internal const string VendorPath = "device/studiox/vendor/";
    public static bool HyperRamControllerAvailable
    {
        get;
    } =
        HasResource("hram_ip.vx") && HasResource("hram_ip.sdc");

    private static bool HasResource(string name) =>
        typeof(Ag32PeripheralSupport).Assembly.GetManifestResourceInfo(Prefix + name) is not null;

    internal static byte[] Resource(string name)
    {
        using var stream = typeof(Ag32PeripheralSupport).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new StudioXException("AG32_PERIPHERAL_RESOURCE", "缺少原厂外设资源：" + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static Dictionary<string, byte[]> Drivers() => typeof(Ag32PeripheralSupport).Assembly.GetManifestResourceNames()
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(name => name[Prefix.Length..]).Where(name => !name.EndsWith(".vx", StringComparison.Ordinal) &&
            !name.EndsWith(".asf", StringComparison.Ordinal) && !name.EndsWith(".sdc", StringComparison.Ordinal))
        .ToDictionary(name => VendorPath + name, Resource, StringComparer.Ordinal);

    internal static void VerifySdk(string root)
    {
        using var provenance = JsonDocument.Parse(Resource("provenance.json"));
        foreach (var item in provenance.RootElement.GetProperty("sdkHeaders").EnumerateObject())
        {
            var path = PathBoundary.Resolve(root, "device/sdk/include/" + item.Name);
            if (!File.Exists(path) || !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(item.Value.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new StudioXException("AG32_PERIPHERAL_SDK", "工程 SDK 与完整外设驱动版本不匹配：" + item.Name + "。请恢复原厂头文件，手动修改不会被覆盖。");
            }
        }
    }

    private static readonly Lazy<Dictionary<string, Ag32AnalogPin[]>> AnalogTables = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, Ag32AnalogPin[]>>(Resource("analog-pins.json"), JsonStore.Options)!);

    public static Ag32AnalogPin[] Pins(string deviceId)
    {
        var target = Ag32DeviceCatalog.Require(deviceId).TargetDevice;
        // VF 数据来自 AGM 引脚工作簿。VH 使用 AG32VH 系列应用指南第 5、6 页逐脚核对。
        var key = target switch
        {
            "AGRV2KQ32" => "32",
            "AGRV2KL48" => "48",
            "AGRV2KL64" => "64",
            "AGRV2KL100" => "100",
            "AGRV2KL64H" or "AGRV2KL100H" => target,
            _ => throw new StudioXException("AG32_ANALOG_CHANNEL", "尚未核实模拟引脚：" + target)
        };
        return [.. AnalogTables.Value[key]];
    }

    internal static Ag32AnalogSettings Read(byte[] source)
    {
        var lines = Encoding.UTF8.GetString(source).Split('\n').Where(line => line.Contains(Marker, StringComparison.Ordinal)).ToArray();
        if (lines.Length == 0)
        {
            return new();
        }
        var match = Regex.Match(lines[0].Trim(), @"\A#@StudioX:ANALOG adc=0x([0-9A-Fa-f]{4}) dac=([0-3]) cmp=([01])\z", RegexOptions.CultureInvariant);
        if (lines.Length != 1 || !match.Success)
        {
            throw new StudioXException("AG32_ANALOG_CONFIG", "模拟配置应为唯一的 #@StudioX:ANALOG adc=0x0001 dac=0 cmp=0 注释。");
        }
        var dac = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return new(true, uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            (dac & 1) != 0, (dac & 2) != 0, match.Groups[3].Value == "1");
    }

    internal static byte[] WithSettings(byte[] source, Ag32AnalogSettings? settings)
    {
        if (settings is null)
        {
            return source;
        }
        if (settings.AdcChannels > 0xffff)
        {
            throw new StudioXException("AG32_ANALOG_CHANNEL", "ADC 外部通道掩码仅允许 0–15。");
        }
        _ = Read(source); // 不覆盖畸形或重复标记。
        var bom = source is [0xef, 0xbb, 0xbf, ..];
        var text = Encoding.UTF8.GetString(bom ? source[3..] : source);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        text = Regex.Replace(text, @"(?m)^[ \t]*#@StudioX:ANALOG[^\r\n]*(?:\r?\n|$)", "");
        if (settings.Enabled)
        {
            text = text.TrimEnd('\r', '\n') + newline + $"{Marker} adc=0x{settings.AdcChannels:X4} dac={(settings.Dac0 ? 1 : 0) + (settings.Dac1 ? 2 : 0)} cmp={(settings.Comparator ? 1 : 0)}" + newline;
        }
        var bytes = Encoding.UTF8.GetBytes(text);
        return bom ? [0xef, 0xbb, 0xbf, .. bytes] : bytes;
    }

    public static Ag32AnalogPin[] ReservedPins(string deviceId, Ag32AnalogSettings settings)
        => ReservedPins(deviceId, settings, null);

    public static Ag32AnalogPin[] ReservedPins(string deviceId, Ag32AnalogSettings settings, Ag32Setup? setup)
    {
        if (!settings.Enabled)
        {
            return [];
        }
        return Pins(deviceId).Where(pin => pin.ActiveFunctions(settings, setup).Length != 0).ToArray();
    }

    internal static void Validate(string deviceId, byte[] source, IReadOnlyList<Ag32PinAssignment> assignments)
    {
        var settings = Read(source);
        ValidateSettings(deviceId, settings);
        var system = Ag32SystemPeripherals.Read(source);
        system.Setup.Validate(deviceId, assignments, settings, Ag32UartInitialization.Read(source),
            Ag32BusInitialization.Read(source), Ag32TimerInitialization.Read(source), system, new Ag32PinPlanDocument(source).Clocks);
        system.Setup.CheckUsbDirective(source);
        foreach (var pin in ReservedPins(deviceId, settings, system.Setup))
        {
            if (assignments.Any(assignment => assignment.PinNumber == pin.Pin))
            {
                throw new StudioXException("AG32_ANALOG_PIN", $"PIN_{pin.Pin} 已预留给 {pin.ActiveFunctions(settings,system.Setup)}，请移除此脚的数字功能映射。");
            }
        }
    }

    public static void ValidateSettings(string deviceId, Ag32AnalogSettings settings)
    {
        if (!settings.Enabled)
        {
            return;
        }
        var pins = Pins(deviceId);
        if (settings.Dac0 && !pins.Any(pin => pin.Supports("DAC0")) ||
            settings.Dac1 && !pins.Any(pin => pin.Supports("DAC1")) ||
            settings.Comparator && !pins.Any(pin => pin.ComparatorInput is not null))

        {
            throw new StudioXException("AG32_ANALOG_CHANNEL", deviceId + " 没有引出所选 DAC 或比较器输入，请取消对应选项。");
        }

        var available = pins.Aggregate(0u, (mask, pin) => pin.AdcChannel is int ch ? mask | (1u << ch) : mask);
        if ((settings.AdcChannels & ~available) != 0)
        {
            throw new StudioXException("AG32_ANALOG_CHANNEL", deviceId + " 没有引出所选 ADC 通道；请按固定引脚表选择。");
        }
    }

    internal static async Task<string[]> PrepareLogicAsync(string run, byte[] source, CancellationToken token)
    {
        if (Ag32Setup.Read(source).Value("RTC", "Clock").StartsWith("LOCAL"))

        {
            await File.AppendAllTextAsync(Path.Combine(run, "pins.ve"), "\nPIN_HSE_OUT CLKLOCAL_RTC:OUTPUT\n", new UTF8Encoding(false), token);
        }
        if (Ag32Setup.Read(source).Units.ContainsKey("HyperRAM"))

        {
            return await Ag32HyperRam.PrepareAsync(run, source, token);
        }
        if (!Read(source).Enabled)
        {
            return [];
        }
        await File.WriteAllBytesAsync(Path.Combine(run, "analog_ip.vx"), Resource("analog_ip.vx"), token);
        await File.WriteAllBytesAsync(Path.Combine(run, "analog_ip.asf"), Resource("analog_ip.asf"), token);
        return ["-m", "analog_ip.vx"];
    }

    internal static string AnalogSdc(string netlist)
    {
        if (!netlist.Contains("module analog_ip(", StringComparison.Ordinal))
        {
            return "";
        }
        // 官方 IP 的域间握手约束；保留原始文件，避免把整个设计设为 false path。
        var original = Encoding.UTF8.GetString(Resource("analog_ip.sdc"));
        return "\n# AGM analog_ip: vendor clock-domain handshake constraints\n" + original[(original.IndexOf("# pio_end", StringComparison.Ordinal) + "# pio_end".Length)..];
    }

    internal static void VerifyLogic(byte[] ve, string netlist, string routed)
    {
        if (!Read(ve).Enabled)
        {
            return;
        }
        var canonical = Encoding.UTF8.GetString(Resource("analog_ip.vx")).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!netlist.Replace("\r\n", "\n", StringComparison.Ordinal).Contains(canonical, StringComparison.Ordinal))
        {
            throw new StudioXException("AG32_ANALOG_LOGIC", "生成网表未包含匹配驱动的完整原厂模拟 IP。");
        }
        var locations = new[] { ("adc", 0, 7), ("adc", 1, 8), ("adc", 2, 9), ("dac", 3, 11), ("dac", 4, 12) };
        foreach (var (kind, index, y) in locations)
        {
            var instance = $@"\macro_inst|gen_per[{index}].gen_{kind}.{kind}_inst|{kind}_inst";
            if (!Regex.IsMatch(routed, @"\balta_" + kind + @"\s+" + Regex.Escape(instance) + @"\s*\(") ||
                !Regex.IsMatch(routed, @"\bdefparam\s+" + Regex.Escape(instance) + @"\s*\.coord_x\s*=\s*22\s*;") ||
                !Regex.IsMatch(routed, @"\bdefparam\s+" + Regex.Escape(instance) + @"\s*\.coord_y\s*=\s*" + y + @"\s*;"))
            {
                throw new StudioXException("AG32_ANALOG_LOGIC", $"模拟硬核 {kind}/{index} 的实际位置不匹配原厂约束。");
            }
        }
        if (Regex.Matches(routed, @"\balta_cmp\s+\\macro_inst\|").Count != 1)
        {
            throw new StudioXException("AG32_ANALOG_LOGIC", "实际网表缺少 CMP 硬核。");
        }
    }
}
