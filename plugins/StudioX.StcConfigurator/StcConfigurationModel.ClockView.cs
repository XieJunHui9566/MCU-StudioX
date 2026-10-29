namespace StudioX.StcConfigurator;

using System.Globalization;
using StudioX.Extensions.Abstractions;

public sealed partial class StcConfigurationModel
{
    private PluginMcuClock[] ClockView()
    {
        string Frequency(decimal? hz) => hz is null ? "尚未设置" : hz >= 1000000 ? (hz / 1000000m)?.ToString("0.######", CultureInfo.InvariantCulture) + " MHz" :
            hz >= 1000 ? (hz / 1000m)?.ToString("0.###", CultureInfo.InvariantCulture) + " kHz" : hz?.ToString("0.###", CultureInfo.InvariantCulture) + " Hz";
        var source = Text(Clock("SYS")!, "inputId");
        var selected = source.Length > 0;
        var nodes = new List<PluginMcuClock>
        {
            new("IRC", "内部 IRC", [], 30, 104, source == "IRC" ? Frequency(SourceClockHz) : "未选用", "source", "oscillator", "ISP 校准目标", source == "IRC"),
            new("XTAL", "外部晶振", [], 30, 354, source == "XTAL" ? Frequency(SourceClockHz) : "未选用", "source", "crystal", "XTAL1 / XTAL2 · P1.7 / P1.6", source == "XTAL"),
            new("SOURCE_MUX", "时钟选择", ["IRC", "XTAL"], 270, 188, selected ? (source == "IRC" ? "内部 IRC" : "外部晶振") : "尚未选择", "multiplexer", "", "ISP 硬件选项", selected, selected ? [source] : []),
            new("SYS", "系统分频", ["SOURCE_MUX"], 450, 220, Frequency(ClockHz), "divider", "÷ " + SystemDivider, "CLK_DIV.CLKS[2:0]", selected),
            new("SYSCLK", "SYSCLK", ["SYS"], 632, 231, Frequency(ClockHz), "junction", "", "", selected),
            new("CPU", "CPU", ["SYSCLK"], 1010, 48, Frequency(ClockHz), "sink", "", "1T 内核", selected)
        };
        if (extended)
        {
            return PeripheralClockView(nodes);
        }
        for (var timer = 0; timer < 2; timer++)
        {
            var id = "T" + timer;
            var enabled = Peripheral("TIMER" + timer) is not null;
            var divider = Clock(id)?["parameters"]?["divider"]?.GetValue<int>() ?? 1;
            var y = 178 + timer * 140;
            nodes.Add(new(id + "_CLK", "计数时钟", ["SYSCLK"], 810, y, enabled ? Frequency(ClockHz / (decimal)divider) : "未启用", "divider",
                enabled ? "÷ " + divider : "÷ 1 / 12", "AUXR.T" + timer + "x12", enabled));
            nodes.Add(new(id, "Timer " + timer, [id + "_CLK"], 1010, y, enabled ? "已配置 · 停止" : "未启用", "sink", "", "16 位自动重装", enabled));
        }
        var uart = Peripheral("UART1");
        var uartEnabled = uart is not null;
        var baud = uartEnabled && ClockHz is { } hz ? Baud(uart!, hz).Rate.ToString("0.###", CultureInfo.InvariantCulture) + " baud" : "未启用";
        nodes.Add(new("T2_CLK", "Timer 2 时钟", ["SYSCLK"], 810, 458, uartEnabled ? Frequency(ClockHz) : "未启用", "divider", "÷ 1", "AUXR.T2x12 · 1T", uartEnabled));
        nodes.Add(new("T2", "Timer 2 → UART1", ["T2_CLK"], 1010, 458, baud, "sink", "", "溢出频率 ÷ 4 → 波特率", uartEnabled));
        return nodes.ToArray();
    }
}
