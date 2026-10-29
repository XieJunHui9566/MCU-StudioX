namespace StudioX.StcConfigurator;

using System.Globalization;
using StudioX.Extensions.Abstractions;

public sealed partial class StcConfigurationModel
{
    private PluginMcuClock[] PeripheralClockView(List<PluginMcuClock> nodes)
    {
        string F(decimal? hz) => hz is null ? "待确认" : hz >= 1000000 ? (hz / 1000000m)?.ToString("0.######", CultureInfo.InvariantCulture) + " MHz" : hz >= 1000 ? (hz / 1000m)?.ToString("0.###", CultureInfo.InvariantCulture) + " kHz" : hz?.ToString("0.###", CultureInfo.InvariantCulture) + " Hz";
        for (var index = 0; index < 3; index++)
        {
            var timer = Peripheral("TIMER" + index);
            var uart = Enumerable.Range(1, 2).Where(u => Parameter("UART" + u, "clockSource") == "T" + index).ToArray();
            var active = timer is not null || uart.Length > 0;
            var external = Parameter("TIMER" + index, "countMode") == "external";
            var divider = Clock("T" + index)?["parameters"]?["divider"]?.GetValue<int>() ?? 1;
            var y = 190 + 180 * index;
            if (external)
            {
                nodes.Add(new("EXT" + index, CounterPins[index] + " 输入", [], 620, y, "外部下降沿", "source", "", "输入频率由外部电路决定", true));
            }
            nodes.Add(new("T" + index + "_CLK", "Timer " + index + " 计数时钟", [external ? "EXT" + index : "SYSCLK"], 810, y, active ? external ? "外部脉冲" : F(ClockHz / (decimal)divider) : "未启用", "divider", external ? "↓" : "÷ " + divider, "AUXR.T" + index + "x12", active));
            var value = !active ? "未启用" : uart.Length > 0 ? "波特率时钟运行" : "已配置 · 停止";
            var detail = uart.Length > 0 ? string.Join(" + ", uart.Select(u => "UART" + u)) : "16 位自动重装";
            if (timer is not null && ClockHz is { } hz)
            {
                var ticks = ExtendedTicks(timer, hz);
                detail = ticks + " 计数/溢出" + (timer["parameters"]!["clockOutput"]!.GetValue<bool>() ? " · CLKO ÷2" : "");
            }
            nodes.Add(new("T" + index, "Timer " + index, ["T" + index + "_CLK"], 1010, y, value, "sink", "", detail, active));
        }
        for (var index = 1; index <= 2; index++)
        {
            var uart = Peripheral("UART" + index);
            var active = uart is not null;
            var source = Parameter("UART" + index, "clockSource", index == 1 ? "T1" : "T2");
            var rate = active && ClockHz is { } hz ? ExtendedBaud(uart!, hz).Rate.ToString("0.###", CultureInfo.InvariantCulture) + " baud" : "未启用";
            nodes.Add(new("UART" + index, "UART " + index, index == 1 ? ["T1", "T2"] : ["T2"], 1260, index == 1 ? 370 : 550, rate, "sink", "", "溢出 ÷4 · " + source, active, active ? [source] : []));
        }
        var pca = Peripheral("PCA");
        var pcaSource = Parameter("PCA", "clockSource", "12");
        decimal? pcaHz = null;
        if (int.TryParse(pcaSource, out var pcaDivider))
        {
            pcaHz = ClockHz / (decimal)pcaDivider;
        }
        else if (pcaSource == "T0" && Peripheral("TIMER0") is { } timer0 && Parameter("TIMER0", "countMode") == "internal" && ClockHz is { } pcaSystemHz)
        {
            var ticks = ExtendedTicks(timer0, pcaSystemHz);
            if (ticks > 0)
            {
                pcaHz = pcaSystemHz / (ticks * (decimal)Number(timer0["parameters"]!, "divider"));
            }
        }
        if (pcaSource == "ECI")
        {
            nodes.Add(new("ECI", "PCA 外部 ECI", [], 620, 730, "外部下降沿", "source", "", "最高 SYSCLK /2", pca is not null));
        }
        nodes.Add(new("PCA_CLK", "PCA 计数时钟", [pcaSource == "ECI" ? "ECI" : pcaSource == "T0" ? "T0" : "SYSCLK"], 810, 730, pca is null ? "未启用" : F(pcaHz), "divider", pcaDivider > 0 ? "÷ " + pcaDivider : pcaSource, "CMOD.CPS[2:0]", pca is not null));
        nodes.Add(new("PCA", "PCA · CCP0/1/2", ["PCA_CLK"], 1010, 730, pca is null ? "未启用" : "已配置 · 停止", "sink", "", "捕获 / 比较 / 8 位 PWM", pca is not null));
        var spi = Peripheral("SPI");
        var spiDivider = spi is null ? 4 : Number(spi["parameters"]!, "divider");
        nodes.Add(new("SPI", "SPI 主机 SCLK", ["SYSCLK"], 810, 910, spi is null ? "未启用" : F(ClockHz / (decimal)spiDivider), "divider", "÷ " + spiDivider, "SPCTL.SPR[1:0]", spi is not null));
        var adc = Peripheral("ADC");
        var cycles = adc is null ? 540 : Number(adc["parameters"]!, "cycles");
        var internalSelected = Text(Clock("SYS")!, "inputId") == "IRC";
        // ADC 使用未分频 IRC，即使 CPU 选了外部晶振也不能冒用晶振频率计算 ADC 速度。
        nodes.Add(new("ADC_IRC", "ADC 内部 IRC", [], 570, 1090, internalSelected ? F(SourceClockHz) : "IRC 校准值未声明", "source", "oscillator", "不经 CLK_DIV", adc is not null));
        nodes.Add(new("ADC", "ADC · 10 位", ["ADC_IRC"], 810, 1090, adc is null ? "未启用" : internalSelected ? F(SourceClockHz / (decimal)cycles) + " 转换/秒" : "速度取决于内部 IRC", "divider", "÷ " + cycles, "ADC_CONTR.SPEED[1:0]", adc is not null));
        var wdt = Peripheral("WDT");
        var wdtDivider = wdt is null ? 2 : Number(wdt["parameters"]!, "divider");
        nodes.Add(new("WDT", "看门狗超时", ["SYSCLK"], 810, 1270, wdt is null ? "未启用" : ClockHz is > 0 ? (393216m * wdtDivider / ClockHz)?.ToString("0.###", CultureInfo.InvariantCulture) + " s" : "待确认", "divider", "÷12 / " + wdtDivider, "15 位计数器 · 应用喂狗", wdt is not null));
        nodes.Add(new("WKT", "掉电唤醒 · 独立 RC", [], 1260, 1090, Peripheral("WKT") is null ? "未启用" : Parameter("WKT", "counts") + " 次计数", "source", "oscillator", "约 488 µs/次 · 非精密时钟", Peripheral("WKT") is not null));
        return [.. nodes];
    }
}
