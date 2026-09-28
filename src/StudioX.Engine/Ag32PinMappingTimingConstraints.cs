namespace StudioX.Engine;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>基础映射的片内传播预算；不推测外部器件的采样时钟、建立时间或保持时间。</summary>
internal static class Ag32PinMappingTimingConstraints
{
    internal static string Render(string header, string netlist)
    {
        var cells = Regex.Matches(netlist,
            @"\b(?<type>alta_rio|alta_rv32|alta_pllve|alta_gclksw|alta_gclkgen)\s+(?<name>\\\S+|[A-Za-z_][A-Za-z0-9_$]*)\s*\((?<body>[\s\S]*?)\);")
            .Select(match => new Cell(match.Groups["type"].Value, match.Groups["name"].Value.TrimStart('\\'),
                Regex.Matches(match.Groups["body"].Value, @"\.(\w+)\s*\(\s*([^()]*)\s*\)")
                    .ToDictionary(port => port.Groups[1].Value, port => Regex.Replace(port.Groups[2].Value, @"\s+", ""))))
            .ToArray();
        var aliases = Regex.Matches(netlist, @"\bassign\s+(\w+)\s*=\s*([\w\[\]]+)\s*;")
            .ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
        var inputVectors = Regex.Matches(netlist, @"\bwire\s+\[[0-9]+:0\]\s+(gpio[0-9]+_io_in)\s*=\s*(\{[^{};]+\})\s*;")
            .ToDictionary(match => match.Groups[1].Value, match => Regex.Replace(match.Groups[2].Value, @"\s+", ""));
        var systemHz = new[] { Frequency("HSI"), Frequency("HSE"), Frequency("PLL") }.Max();
        if (systemHz == 0) { throw new StudioXException("AG32_MAPPING_CLOCK", "缺少有效的基础映射时钟频率。"); }
        var busHz = Frequency("BUS");
        if (busHz == 0) { busHz = systemHz; }
        var result = new StringBuilder();
        result.AppendLine("# StudioX basic-mapping routing budget v1: one BUS/SYS cycle, in ns.");
        result.AppendLine("# This is an internal propagation budget, not external-device setup/hold timing.");
        result.AppendLine("# Only actual direct GPIO and fixed clock connections are constrained; no false paths.");
        var mcu = cells.SingleOrDefault(cell => cell.Type == "alta_rv32" && cell.Name == "rv32");
        if (mcu is not null)
        {
            foreach (var io in cells.Where(cell => cell.Type == "alta_rio" && Regex.IsMatch(cell.Name, @"^PIN_[0-9]+_iobuf$")))
            {
                foreach (var (sink, kind) in new[] { ("datain", "data"), ("oe", "en") })
                {
                    var signal = Resolve(io.Port(sink));
                    var gpio = Regex.Match(signal, @"^(gpio[0-9]+_io_out_" + kind + @")\[([0-9]+)\]$");
                    if (gpio.Success && mcu.Port(gpio.Groups[1].Value) == gpio.Groups[1].Value)
                    {
                        Add("rv32|" + signal, io.Name + "|" + sink, busHz, "GPIO / BUS");
                    }
                }
                var input = Resolve(io.Port("combout"));
                if (string.IsNullOrEmpty(input)) { continue; }
                foreach (var (port, connection) in mcu.Ports.Where(pair => Regex.IsMatch(pair.Key, @"^gpio[0-9]+_io_in$")))
                {
                    var expression = inputVectors.GetValueOrDefault(connection, connection);
                    if (!expression.StartsWith('{') || !expression.EndsWith('}')) { continue; }
                    var bits = expression[1..^1].Split(',');
                    for (var bit = 0; bit < bits.Length; bit++)
                    {
                        if (Resolve(bits[bits.Length - 1 - bit]) == input)
                        {
                            Add(io.Name + "|combout", "rv32|" + port + "[" + bit + "]", busHz, "GPIO / BUS");
                        }
                    }
                }
            }
        }
        Connect("gclksw_inst", "clkout", "gclksw_gen", "clkin", systemHz, "SYS distribution");
        Connect("pll_inst", "clkout3", "PLL_CLKOUT[3]_gclkgen", "clkin", busHz, "BUS distribution");
        Connect("pll_inst", "clkfbout", "pll_inst", "clkfb", systemHz, "PLL feedback interconnect");
        return result.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);

        ulong Frequency(string name)
        {
            var matches = Regex.Matches(header, @"(?m)^[ \t]*#define[ \t]+BOARD_" + name + @"_FREQUENCY[ \t]+([0-9]+)[ \t]*\r?$");
            if (matches.Count != 1 || !ulong.TryParse(matches[0].Groups[1].Value, out var value) || value > 1_000_000_000)
            {
                throw new StudioXException("AG32_MAPPING_CLOCK", "缺少唯一有效的布线预算频率：" + name);
            }
            return value;
        }
        string Resolve(string signal)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (aliases.TryGetValue(signal, out var next))
            {
                if (!seen.Add(signal)) { throw new StudioXException("AG32_MAPPING_CLOCK", "基础映射存在循环信号别名。"); }
                signal = next;
            }
            return signal;
        }
        void Connect(string sourceName, string sourcePort, string sinkName, string sinkPort, ulong hz, string reason)
        {
            var source = cells.SingleOrDefault(cell => cell.Name == sourceName);
            var sink = cells.SingleOrDefault(cell => cell.Name == sinkName);
            if (source is not null && sink is not null && source.Port(sourcePort).Length > 0 &&
                Resolve(source.Port(sourcePort)) == Resolve(sink.Port(sinkPort)))
            {
                Add(sourceName + "|" + sourcePort, sinkName + "|" + sinkPort, hz, reason);
            }
        }
        void Add(string source, string sink, ulong hz, string reason)
        {
            // 来自锁定转换器的精确实例/端口名，使用 -exact 保留向量下标的含义。
            if (!Regex.IsMatch(source, @"^[A-Za-z0-9_|\[\]]+$") || !Regex.IsMatch(sink, @"^[A-Za-z0-9_|\[\]]+$"))
            {
                throw new StudioXException("AG32_MAPPING_CLOCK", "布线预算端点包含不支持的字符。");
            }
            var period = (1_000_000_000m / hz).ToString("0.#########", CultureInfo.InvariantCulture);
            result.AppendLine("# " + reason);
            result.AppendLine($"set_max_delay -from [get_pins -exact {{{source}}}] -to [get_pins -exact {{{sink}}}] {period}");
        }
    }

    private sealed record Cell(string Type, string Name, Dictionary<string, string> Ports)
    {
        internal string Port(string name) => Ports.GetValueOrDefault(name, "");
    }
}
