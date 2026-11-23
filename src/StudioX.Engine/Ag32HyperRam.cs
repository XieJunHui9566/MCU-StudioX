namespace StudioX.Engine;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>把 VH 封装内的 HyperBus 专用焊盘和 DDR 控制器接到 MCU 存储总线。</summary>
internal static class Ag32HyperRam
{
    private static readonly (string Port, string Pad)[] Pads =
    [
        ("hbus_clk_out", "HRAM_CK"), ("hbus_csn", "HRAM_CSN"), ("hbus_resetn", "HRAM_RESETN"), ("hbus_rwds", "HRAM_RWDS"),
        .. Enumerable.Range(0, 8).Select(bit => ($"hbus_adq[{bit}]", $"HRAM_DQ{bit}"))
    ];

    internal static async Task<string[]> PrepareAsync(string directory, byte[] source, CancellationToken token)
    {
        var system = new Ag32PinPlanDocument(source).Clocks.SysMhz;
        if (system is not (> 0 and <= 200))

        {
            throw Error("请先在时钟页配置最高 200 MHz 的系统时钟。");
        }
        var lines = Encoding.UTF8.GetString(source).Split('\n').Select(line => line.Split('#')[0].Trim());
        if (lines.Any(line => Regex.IsMatch(line, @"^(?:PLLCLK4(?:_PHASE)?|PLL_CLKOUT[04]|hbus_\w+)\b", RegexOptions.IgnoreCase)))

        {
            throw Error("手写 VE 已使用 HyperBus 的端口或 PLL 输出，请先核对配置。");
        }
        var ve = new StringBuilder().AppendLine().AppendLine("PLLCLK4 " + system.Value.ToString(CultureInfo.InvariantCulture))
            .AppendLine("PLLCLK4_PHASE 90").AppendLine("PLL_CLKOUT0 hbus_clk").AppendLine("PLL_CLKOUT4 hbus_clk90");
        foreach (var (port, pad) in Pads)

        {
            ve.AppendLine(port + " " + pad + (pad is "HRAM_CK" or "HRAM_CSN" or "HRAM_RESETN" ? ":OUTPUT" : ""));
        }
        await File.AppendAllTextAsync(Path.Combine(directory, "pins.ve"), ve.ToString(), new UTF8Encoding(false), token);
        await File.WriteAllBytesAsync(Path.Combine(directory, "hram_ip.vx"), Ag32PeripheralSupport.Resource("hram_ip.vx"), token);
        // 调整 RWDS 输入采样时钟相对 DQ 的实际传播延迟，约束仍使用原厂的 ±0.4 ns 窗口。
        await File.WriteAllTextAsync(Path.Combine(directory, "hram_ip.asf"),
            "set_instance_assignment -name IN_DATA_DELAY -to hbus_rwds 3'b101 -extension\n", token);
        return ["-m", "hram_ip.vx"];
    }

    internal static string Timing(string header, string netlist)
    {
        if (!netlist.Contains("module hram_ip(", StringComparison.Ordinal))
        {
            return "";
        }
        var frequency = Regex.Match(header, @"(?m)^#define BOARD_PLL_FREQUENCY (\d+)\b");
        if (!decimal.TryParse(frequency.Groups[1].Value, CultureInfo.InvariantCulture, out var hz) || hz <= 0)

        {
            throw Error("缺少 HyperBus 的实际系统时钟。");
        }
        var halfPeriod = (500_000_000m / hz).ToString("0.#########", CultureInfo.InvariantCulture);
        var result = new StringBuilder().AppendLine().Append(Encoding.UTF8.GetString(Ag32PeripheralSupport.Resource("hram_ip.sdc")));
        // CS、RESET 和转发时钟的 DDR 输出级必须在一个半周期内到达。
        foreach (var sink in new[]{"hbus_csn~O|datain", "hbus_resetn~O|datain",
            "macro_inst|u_ahb2hbus|gen_ddio[0].ddio_clk|auto_generated|ddio_outa[0]__DDIOMUX|B",
            "macro_inst|u_ahb2hbus|gen_ddio[0].ddio_clk|auto_generated|ddio_outa[0]__DDIOMUX|C"})

        {
            result.AppendLine($"set_max_delay -to [get_pins -exact {{{sink}}}] {halfPeriod}");
        }
        return result.ToString();
    }

    internal static void Verify(string vex, string io, string routed)
    {
        var strobe = Regex.Match(routed, @"\balta_rio\s+(?<cell>\\[^\s]+)\s*\([^;]*\.padio\(hbus_rwds\)[^;]*\);");
        if (!strobe.Success || !Regex.IsMatch(routed, @"defparam\s+" + Regex.Escape(strobe.Groups["cell"].Value) + @"\s*\.IN_DATA_DELAY\s*=\s*3'b101;"))

        {
            throw Error("布局后的 RWDS 专用输入延迟与控制器配置不一致。");
        }
        foreach (var (port, pad) in Pads)
        {
            if (!Regex.IsMatch(vex, @"(?m)^" + Regex.Escape(port) + @"[ \t]+" + pad + @"[ \t]*\r?$") ||
                !Regex.IsMatch(io, @"(?m)^set_location_assignment -to " + Regex.Escape(port) + " " + pad + @"[ \t]*\r?$") ||
                !Regex.IsMatch(routed, @"\.padio\(\s*" + Regex.Escape(port) + @"\s*\)"))

            {
                throw Error($"布局未保留 HyperBus 专用连接 {port} → {pad}。");
            }
        }
    }

    private static StudioXException Error(string message) => new("AG32_HYPERRAM", message);
}
