namespace StudioX.Engine;

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>为厂商网表补充准确输入时序，并核对布局后保留的 PLL 分频位与 MCU 时钟连接。</summary>
internal static class Ag32PinMappingClockVerification
{
    private const string SdcRelativePath = "studiox-clocks.sdc";

    internal static async Task CreateSdcAsync(string build, CancellationToken token, bool basicMapping = false)
    {
        var header = await ReadAsync(build, "pins.hx", token);
        var text = RenderSdc(header);
        if (basicMapping)
        {
            text += Ag32PinMappingTimingConstraints.Render(header, await ReadAsync(build, "pins.vx", token));
        }
        await File.WriteAllTextAsync(PathBoundary.Resolve(build, SdcRelativePath), text, token);
    }

    internal static async Task<string[]> VerifyAsync(string build, CancellationToken token)
    {
        var source = await ReadAsync(build, "pins.vx", token);
        var routed = await ReadAsync(build, "pins_routed.v", token);
        var sourceParameters = PllParameters(source);
        var routedParameters = PllParameters(routed);
        var sourceCells = ClockCells(source);
        var routedCells = ClockCells(routed);
        var sourcePll = sourceCells.ContainsKey("alta_pllve:pll_inst");
        var routedPll = routedCells.ContainsKey("alta_pllve:pll_inst");
        if (sourcePll != routedPll || sourceParameters.Count > 0 && !sourcePll || routedParameters.Count > 0 && !routedPll)
        {
            throw Error("源网表与最终网表的 PLL 实例不一致。");
        }
        foreach (var (parameter, value) in sourceParameters)
        {
            if (!routedParameters.TryGetValue(parameter, out var actual) || actual != value)
            {
                throw Error($"最终 PLL 位参数 {parameter} 未保留厂商转换结果；禁止下载未经确认的时钟配置。");
            }
        }
        if (sourcePll && sourceParameters.Count == 0)
        {
            throw Error("源 PLL 缺少可核对的数字配置参数。");
        }
        var aliases = Aliases(source);
        foreach (var (key, ports) in sourceCells)
        {
            if (!routedCells.TryGetValue(key, out var actual))
            {
                throw Error("最终网表缺少时钟路径实例：" + key);
            }
            foreach (var (port, net) in ports)
            {
                if (!actual.TryGetValue(port, out var actualNet) || CanonicalNet(net, aliases) != CanonicalNet(actualNet, aliases))
                {
                    throw Error($"时钟路径 {key}.{port} 与源设计不一致。");
                }
            }
        }
        // CLKIN_FREQ 是时序注解，不是 PLL 硬件位字段；布局器可移除它，数字分频参数须逐项保留。
        var header = await ReadAsync(build, "pins.hx", token);
        var sdc = await ReadAsync(build, SdcRelativePath, token);
        if (sdc != RenderSdc(header) + Ag32PinMappingTimingConstraints.Render(header, source))
        {
            throw Error("实际输入时序约束与厂商生成的频率头文件不一致。");
        }
        var frequencies = InputFrequencies(header);
        return [sourcePll
            ? $"最终 PLL 的 {sourceParameters.Count} 项数字位参数与转换器结果一致；MCU SYS/BUS 时钟路径已核对。"
            : "源设计未使用 PLL，最终网表也未引入 PLL；时钟路径已核对。",
            $"输入时序：HSI {frequencies["HSI"]} Hz / HSE {frequencies["HSE"]} Hz / OSC {frequencies["OSC"]} Hz。此值来自厂商配置，不代表实板晶振测量。"];
    }

    private static Dictionary<string, BigInteger> PllParameters(string text)
    {
        var result = new Dictionary<string, BigInteger>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(text, @"\bdefparam\s+pll_inst\.(?<name>\w+)\s*=\s*(?<value>[^;]+);", RegexOptions.CultureInvariant))
        {
            var value = match.Groups["value"].Value.Trim();
            if (value.StartsWith('"'))
            {
                continue;
            }
            var literal = Regex.Match(value, @"^(?:[0-9]+)?'[sS]?(?<base>[bBdDhH])(?<digits>[0-9a-fA-F_]+)$", RegexOptions.CultureInvariant);
            BigInteger number;
            if (literal.Success)
            {
                var radix = char.ToLowerInvariant(literal.Groups["base"].Value[0]) switch
                {
                    'b' => 2,
                    'd' => 10,
                    'h' => 16,
                    _ => 0
                };
                number = BigInteger.Zero;
                foreach (var character in literal.Groups["digits"].Value.Replace("_", "", StringComparison.Ordinal))
                {
                    var digit = character <= '9' ? character - '0' : char.ToLowerInvariant(character) - 'a' + 10;
                    if (digit < 0 || digit >= radix)
                    {
                        throw Error("PLL 参数包含无效数字：" + match.Groups["name"].Value);
                    }
                    number = number * radix + digit;
                }
            }
            else if (!BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            {
                throw Error("PLL 参数不是可核对的数字：" + match.Groups["name"].Value);
            }
            if (!result.TryAdd(match.Groups["name"].Value, number))
            {
                throw Error("PLL 参数存在重复定义：" + match.Groups["name"].Value);
            }
        }
        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> ClockCells(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var instances = Regex.Matches(text,
            @"\b(?<type>alta_pllve|alta_gclksw|alta_gclkgen|alta_io_gclk|alta_rv32)\s+(?<name>\\\S+|[A-Za-z_][A-Za-z0-9_$]*)\s*\((?<body>[\s\S]*?)\);",
            RegexOptions.CultureInvariant);
        foreach (Match instance in instances)
        {
            var type = instance.Groups["type"].Value;
            var ports = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match port in Regex.Matches(instance.Groups["body"].Value, @"\.(?<name>\w+)\s*\(\s*(?<net>[^()]*)\s*\)", RegexOptions.CultureInvariant))
            {
                var name = port.Groups["name"].Value;
                var selected = type switch
                {
                    "alta_pllve" => name is "clkin" or "clkfb" or "clkfbout" or "clkout0" or "clkout1" or "clkout2" or "clkout3" or "clkout4",
                    "alta_gclksw" => name is "clkin0" or "clkin1" or "clkin2" or "clkin3" or "clkout",
                    "alta_gclkgen" => name is "clkin" or "clkout",
                    "alta_io_gclk" => name is "inclk" or "outclk",
                    "alta_rv32" => name == "sys_clk",
                    _ => false
                };
                if (selected && !ports.TryAdd(name, StripWhitespace(port.Groups["net"].Value)))
                {
                    throw Error("时钟实例端口重复：" + type + "." + name);
                }
            }
            var key = type + ":" + instance.Groups["name"].Value.TrimStart('\\');
            if (!result.TryAdd(key, ports))
            {
                throw Error("时钟路径实例重复：" + key);
            }
        }
        return result;
    }

    private static Dictionary<string, HashSet<string>> Aliases(string source)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(source, @"\bassign\s+(?<left>[A-Za-z_]\w*)\s*=\s*(?<right>[A-Za-z_]\w*)\s*;", RegexOptions.CultureInvariant))
        {
            var left = match.Groups["left"].Value;
            var right = match.Groups["right"].Value;
            if (!result.TryGetValue(left, out var leftSet))
            {
                leftSet = [left];
            }
            if (!result.TryGetValue(right, out var rightSet))
            {
                rightSet = [right];
            }
            leftSet.UnionWith(rightSet);
            foreach (var item in leftSet)
            {
                result[item] = leftSet;
            }
        }
        return result;
    }

    private static string CanonicalNet(string net, Dictionary<string, HashSet<string>> aliases) =>
        aliases.TryGetValue(net, out var names) ? names.Order(StringComparer.Ordinal).First() : net;

    private static string StripWhitespace(string value) => Regex.Replace(value, @"\s+", "", RegexOptions.CultureInvariant);

    private static Dictionary<string, ulong> InputFrequencies(string header)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        foreach (var name in new[] { "HSI", "HSE", "OSC" })
        {
            var matches = Regex.Matches(header, @"^[ \t]*#define[ \t]+BOARD_" + name + @"_FREQUENCY[ \t]+([0-9]+)[ \t]*\r?$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (matches.Count != 1 || !ulong.TryParse(matches[0].Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var frequency) || frequency > 1_000_000_000)
            {
                throw Error("厂商头文件缺少有效且唯一的输入频率：" + name);
            }
            result[name] = frequency;
        }
        return result;
    }

    private static string RenderSdc(string header)
    {
        var frequencies = InputFrequencies(header);
        var result = new StringBuilder("# StudioX generated input clocks from the locked vendor header; do not edit.\n");
        foreach (var name in new[] { "HSI", "HSE", "OSC" })
        {
            if (frequencies[name] == 0)
            {
                continue;
            }
            var period = (1_000_000_000m / frequencies[name]).ToString("0.#########", CultureInfo.InvariantCulture);
            // 仅插入经过数字校验的周期与固定端口名，不接收用户 Tcl 或路径表达式。
            result.AppendLine($"create_clock -name PIN_{name} -period {period} [get_ports PIN_{name}]");
            result.AppendLine($"set_clock_groups -asynchronous -group PIN_{name}");
        }
        result.AppendLine("derive_pll_clocks -create_base_clocks");
        return result.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static async Task<string> ReadAsync(string build, string relative, CancellationToken token)
    {
        var path = PathBoundary.Resolve(build, relative);
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 8 * 1024 * 1024)
        {
            throw Error("缺少有效的时钟证据文件：" + relative);
        }
        var bytes = await File.ReadAllBytesAsync(path, token);
        if (bytes.Length > 8 * 1024 * 1024)
        {
            throw Error("时钟证据文件超过大小限制：" + relative);
        }
        return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }

    private static StudioXException Error(string message) => new("AG32_MAPPING_CLOCK", message);
}
