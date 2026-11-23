namespace StudioX.Engine;

using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public static class Ag32BusInitialization
{
    private static readonly Regex Lines = new(@"(?<![^\r\n])[ \t]*#@StudioX:BUS[^\r\n]*(?:\r\n|\n|\r|\z)");
    private static readonly Regex SpiLine = new(@"\A#@StudioX:BUS (SPI[01]) divider=(\d+) endian=(little|big)\z");
    private static readonly Regex RateLine = new(@"\A#@StudioX:BUS (I2C[01]|CAN0) bitrate=(\d+)(?: sample=(\d+))?\z");
    public static bool Supports(string? module) => module is "SPI0" or "SPI1" or "I2C0" or "I2C1" or "CAN0";
    private static StudioXException Error(string message) => new("AG32_BUS_INIT", message);

    internal static Ag32BusSettings[] Read(byte[] source)
    {
        var settings = new List<Ag32BusSettings>();
        foreach (Match line in Lines.Matches(Encoding.UTF8.GetString(source).TrimStart('\uFEFF')))
        {
            var text = line.Value.Trim();
            var irq = Regex.Match(text, @" irq=([0-9]+) priority=([0-9]+)\z");
            var parameters = irq.Success ? text[..irq.Index] : text;
            var extra = Regex.Match(parameters, @" (?:dma=(off|[0-7]),(off|[0-7])(?:,(?<dp>[1-9]|1[0-5]))?|filter=(STD|EXT|STD_MASK|EXT_MASK),([0-9A-Fa-f]{1,8}),([0-9A-Fa-f]{1,8}))\z");
            if (extra.Success)
            {
                parameters = parameters[..extra.Index];
            }
            var spi = SpiLine.Match(parameters);
            var match = spi.Success ? spi : RateLine.Match(parameters);
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out var value))

            {
                throw Error($"无法读取外设配置：{text}");
            }
            int? sample = null;
            if (!spi.Success && match.Groups[3].Success)
            {
                if (!int.TryParse(match.Groups[3].Value, out var number))
                {
                    throw Error($"采样点无效：{text}");
                }
                sample = number;
            }
            var item = spi.Success ? new Ag32BusSettings(match.Groups[1].Value, value, match.Groups[3].Value == "big")
                : new Ag32BusSettings(match.Groups[1].Value, BitRate: value, SamplePointPermille: sample);
            if (extra.Groups[1].Success)

            {
                item = item with
                {
                    RxDmaChannel = Channel(extra.Groups[1].Value),
                    TxDmaChannel = Channel(extra.Groups[2].Value),
                    DmaPriority = extra.Groups["dp"].Success ? int.Parse(extra.Groups["dp"].Value) : 1
                };
            }
            if (extra.Groups[3].Success)

            {
                item = item with
                {
                    FilterMode = extra.Groups[3].Value,
                    FilterId = Convert.ToInt32(extra.Groups[4].Value, 16),
                    CompareMask = Convert.ToInt32(extra.Groups[5].Value, 16)
                };
            }
            if (irq.Success)
            {
                if (!int.TryParse(irq.Groups[1].Value, out var mask) || !int.TryParse(irq.Groups[2].Value, out var priority))

                {
                    throw Error($"{item.Module} 的中断配置无效。");
                }
                item = item with
                {
                    InterruptMask = mask,
                    InterruptPriority = priority
                };
            }
            Validate(item);
            if (settings.Any(s => s.Module == item.Module))
            {
                throw Error($"{item.Module} 初始化配置重复。");
            }
            settings.Add(item);
        }
        if (DmaConflict(settings) is { } conflict)
        {
            throw Error(conflict);
        }
        return settings.OrderBy(s => s.Module, StringComparer.Ordinal).ToArray();
    }

    private static int? Channel(string value) => value == "off" ? null : value[0] - '0';

    public static string? DmaConflict(IEnumerable<Ag32BusSettings> settings)
    {
        if (settings.Where(Ag32SpiDma.Enabled).Select(b => b.DmaPriority).Distinct().Skip(1).Any())

        {
            return "SPI DMA 共用 DMAC0_INTR 向量，请设置相同的中断优先级。";
        }
        var used = new Dictionary<int, string>();
        foreach (var bus in settings)

        {
            foreach (var (channel, direction) in new[] { (bus.RxDmaChannel, "RX"), (bus.TxDmaChannel, "TX") })

            {
                if (channel is int ch && !used.TryAdd(ch, $"{bus.Module} {direction}"))

                {
                    return $"DMA 通道 {ch} 同时分配给 {used[ch]} 和 {bus.Module} {direction}，请选择不同通道。";
                }
            }
        }
        return null;
    }

    internal static byte[] Write(byte[] source, IReadOnlyList<Ag32BusSettings>? settings)
    {
        if (settings is null)
        {
            return source;
        }
        var ordered = settings.OrderBy(s => s.Module, StringComparer.Ordinal).ToArray();
        if (DmaConflict(ordered) is { } conflict)
        {
            throw Error(conflict);
        }
        if (Read(source).SequenceEqual(ordered))
        {
            return source;
        }
        if (ordered.DistinctBy(s => s.Module).Count() != ordered.Length)
        {
            throw Error("外设实例重复。");
        }
        var bom = source is [0xef, 0xbb, 0xbf, ..];
        var text = Encoding.UTF8.GetString(source.AsSpan(bom ? 3 : 0));
        var newline = Regex.Match(text, @"\r\n|\r|\n") is { Success: true } m ? m.Value : "\n";
        var output = new StringBuilder(Lines.Replace(text, ""));
        foreach (var s in ordered)
        {
            Validate(s);
            if (output.Length > 0 && output[^1] is not ('\r' or '\n'))
            {
                output.Append(newline);
            }
            var fields = s.ClockDivider is int divisor ? $"divider={divisor} endian={(s.BigEndian ? "big" : "little")}"
                : $"bitrate={s.BitRate}" + (s.SamplePointPermille is int point ? $" sample={point}" : "");
            output.Append($"#@StudioX:BUS {s.Module} {fields}");
            if (s.RxDmaChannel.HasValue || s.TxDmaChannel.HasValue)

            {
                output.Append($" dma={s.RxDmaChannel?.ToString() ?? "off"},{s.TxDmaChannel?.ToString() ?? "off"}" + (s.DmaPriority == 1 ? "" : $",{s.DmaPriority}"));
            }
            if (s.FilterMode != "ALL")
            {
                output.Append($" filter={s.FilterMode},{s.FilterId:X},{s.CompareMask:X}");
            }
            if (s.InterruptMask != 0 || s.InterruptPriority != 1)

            {
                output.Append($" irq={s.InterruptMask} priority={s.InterruptPriority}");
            }
            output.Append(newline);
        }
        return [.. source.AsSpan(0, bom ? 3 : 0), .. Encoding.UTF8.GetBytes(output.ToString())];
    }

    private static void Validate(Ag32BusSettings item)
    {
        bool valid = item.Module switch
        {
            "SPI0" or "SPI1" => item.ClockDivider is 2 or 4 or 8 or 16 or 32 or 64 or 128 or 256 &&
                item.BitRate is null && item.SamplePointPermille is null,
            "I2C0" or "I2C1" => item.BitRate is 100000 or 400000 or 1000000 && item.SamplePointPermille is null &&
                item.ClockDivider is null && !item.BigEndian,
            "CAN0" => item.BitRate is >= 10000 and <= 1000000 && item.SamplePointPermille is >= 500 and <= 950 &&
                item.ClockDivider is null && !item.BigEndian,
            _ => false
        };
        if (item.RxDmaChannel.HasValue || item.TxDmaChannel.HasValue)

        {
            if (item.ClockDivider is null || item.RxDmaChannel is < 0 or > 7 || item.TxDmaChannel is < 0 or > 7)

            {
                throw Error($"{item.Module} 的 SPI DMA 通道必须在 0～7 范围内。");
            }
        }
        var limit = item.FilterMode.StartsWith("EXT", StringComparison.Ordinal) ? 0x1fffffff : 0x7ff;
        if (item.FilterMode is not ("ALL" or "STD" or "EXT" or "STD_MASK" or "EXT_MASK") ||
            item.FilterMode != "ALL" && item.Module != "CAN0" || item.FilterId < 0 || item.FilterId > limit ||
            item.CompareMask < 0 || item.CompareMask > limit || item.FilterMode == "ALL" && item.FilterId != 0 ||
            !item.FilterMode.EndsWith("_MASK", StringComparison.Ordinal) && item.CompareMask != 0)

        {
            throw Error("CAN 过滤 ID 或掩码无效。标准 ID 为 0～7FF，扩展 ID 为 0～1FFFFFFF（十六进制）。");
        }
        if (!valid || item.DmaPriority is < 1 or > 15 || !Ag32SpiDma.Enabled(item) && item.DmaPriority != 1 || item.InterruptPriority is < 1 or > 15 || item.InterruptMask < 0 ||
            item.InterruptMask > (item.Module == "CAN0" ? 255 : 1))

        {
            throw Error($"{item.Module} 的初始化或中断参数无效。");
        }
    }

    // 仅选择可以精确产生目标位率的时序，再按采样点误差和时间量子数择优。
    private static (int Divisor, int Seg1, int Seg2) CanTiming(uint clock, int rate, int sample, int sjw)
    {
        var best = (Divisor: 0, Seg1: 0, Seg2: 0);
        var error = decimal.MaxValue;
        for (var seg1 = sjw; seg1 <= 16; seg1++)

        {
            for (var seg2 = sjw; seg2 <= 8; seg2++)
            {
                var quanta = 1 + seg1 + seg2;
                var unit = 2L * rate * quanta;
                if (clock % unit != 0 || clock / unit is < 1 or > 64)
                {
                    continue;
                }
                var delta = Math.Abs(1000m * (1 + seg1) / quanta - sample);
                if (delta > error || delta == error && quanta <= 1 + best.Seg1 + best.Seg2)
                {
                    continue;
                }
                best = ((int)(clock / unit), seg1, seg2);
                error = delta;
            }
        }
        if (best.Divisor == 0)
        {
            throw Error($"CAN0 在当前外设时钟 {clock} Hz 下无法精确产生 {rate} bit/s，请调整位率或总线时钟。");
        }
        return best;
    }

    public static string Describe(Ag32BusSettings item, uint clock, Ag32Setup? setup = null)
    {
        Validate(item);
        if (item.ClockDivider is int div)
        {
            return $"SPI 主机 · SCLK 分频 {div} · {(item.BigEndian ? "大端" : "小端")}字节序";
        }
        var rate = item.BitRate!.Value;
        if (item.Module != "CAN0")
        {
            var prescale = (clock + 5L * rate - 1) / (5L * rate);
            if (clock < 5L * rate || prescale > 65536)
            {
                throw Error($"{item.Module} 的总线时钟无法产生所选 SCL。");
            }
            return $"主机 SCL 约 {clock / (5 * prescale)} Hz · SDA 与 SCL 需外部上拉";
        }
        var (_, seg1, seg2) = CanTiming(clock, rate, item.SamplePointPermille!.Value, setup?.Number("CAN0", "SJW") ?? 1);
        return FormattableString.Invariant($"{rate} bit/s · 实际采样点 {100m * (1 + seg1) / (1 + seg1 + seg2):0.#}% · SJW {setup?.Number("CAN0","SJW") ?? 1} TQ · {FilterDescription(item)}");
    }

    internal static int EffectiveMask(Ag32BusSettings item) => item.FilterMode.EndsWith("_MASK", StringComparison.Ordinal)
        ? item.CompareMask : item.FilterMode == "STD" ? 0x7ff : 0x1fffffff;

    private static string FilterDescription(Ag32BusSettings item)
    {
        if (item.FilterMode == "ALL")
        {
            return "接收全部标识符";
        }
        var mask = EffectiveMask(item);
        return $"{(item.FilterMode.StartsWith("STD", StringComparison.Ordinal) ? "标准" : "扩展")}帧匹配 (ID & 0x{mask:X}) == 0x{item.FilterId & mask:X}。中断回调同步核对帧格式，轮询接收可调用 SX_CAN0_MatchesFilter";
    }

    internal static (uint Id, uint Mask)? SecondFilter(Ag32BusSettings item, Ag32Setup? setup)
    {
        if (setup?.Value("CAN0", "Filter") != "双组")
        {
            return null;
        }
        var max = item.FilterMode.StartsWith("STD") ? 0x7ffu : 0x1fffffffu;
        uint Read(string field)
        {
            var text = setup.Value("CAN0", field);
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
            }
            return uint.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var n) && n <= max
                ? n : throw Error("CAN0 第二组 ID 或比较掩码超出所选帧格式范围。");
        }
        if (item.FilterMode == "ALL")
        {
            throw Error("CAN0 双组匹配需先选择标准帧或扩展帧过滤。");
        }
        return (Read("SecondId"), Read("SecondMask"));
    }

    private static string FilterCode(Ag32BusSettings item, Ag32Setup? setup)
    {
        var second = SecondFilter(item, setup);
        if (item.FilterMode == "ALL")
        {
            return "";
        }
        var standard = item.FilterMode.StartsWith("STD", StringComparison.Ordinal);
        var ignore = EffectiveMask(item) ^ (standard ? 0x7ff : 0x1fffffff);
        var args = $"&config.FilterConfig, 0x{ignore:X}u, 0x{item.FilterId:X}u";
        if (second is { } f)

        {
            return standard ? $"\n    CAN_SetStandardDualFilter({args}, 0xffu, 0u, 1u, 0u, 0x{f.Mask ^ 0x7ffu:X}u, 0x{f.Id:X}u, 1u, 0u);"
                            : $"\n    CAN_SetExtendedDualFilter({args}, 0x{f.Mask ^ 0x1fffffffu:X}u, 0x{f.Id:X}u);";
        }
        return standard ? $"\n    CAN_SetStandardSingleFilter({args}, 0xffffu, 0u, 1u, 0u);"
            : $"\n    CAN_SetExtendedSingleFilter({args}, 1u, 0u);";
    }

    public static string InitCode(Ag32BusSettings item, IReadOnlyList<Ag32PinAssignment> pins, uint clock, Ag32Setup? setup = null)
    {
        var description = Describe(item, clock, setup);
        var module = item.Module;
        var comment = $"/* {module} · {description}。 */";
        bool Has(string signal) => pins.Any(p => p.Function == module + "_" + signal);
        void Require(string signal)
        {
            if (!Has(signal))
            {
                throw Error($"{module} 尚未分配 {signal} 引脚。");
            }
        }
        if (item.ClockDivider is int div)
        {
            Require("SCK");
            if (item.TxDmaChannel.HasValue || item.RxDmaChannel.HasValue)
            {
                Require("SI_IO0");
            }
            if (item.RxDmaChannel.HasValue)
            {
                Require("SO_IO1");
            }
            if (!Has("SI_IO0") && !Has("SO_IO1"))
            {
                throw Error($"{module} 至少需要分配 IO0 或 IO1 数据脚。");
            }
            return $"{comment}\nSPI_Init({module}, SPI_CTRL_SCLK_DIV{div});" + (item.BigEndian ? $"\n{module}->CTRL &= ~SPI_CTRL_ENDIAN;" : "") +
                (item.InterruptMask == 0 || Ag32SpiDma.Enabled(item) ? "" : $"\nSPI_ClearInt({module});\nSPI_EnableInt({module});\n" + Ag32InterruptCode.Enable(module, item.InterruptPriority));
        }
        var rate = item.BitRate!.Value;
        if (module != "CAN0")
        {
            Require("SCL");
            Require("SDA");
            // SDK 的 I2C_Init 向下取整可能超过所选 SCL；按运行时 PCLK 向上取整再写原厂预分频接口。
            var irq = item.InterruptMask == 0 ? "" : $"\n        I2C_EnableInt({module});\n" + Ag32InterruptCode.Enable(module, item.InterruptPriority, "        ");
            return $$"""
                {{comment}}
                {
                    const uint32_t pclk = StudioX_PeripheralClockHz();
                    const uint32_t divider = (pclk + {{5 * rate - 1}}u) / {{5 * rate}}u;
                    I2C_Disable({{module}});
                    I2C_DisableInt({{module}});
                    if (pclk >= {{5 * rate}}u && divider <= 65536u)
                    {
                        I2C_SetPrescaler({{module}}, (uint16_t)(divider - 1u));
                        I2C_ClearInt({{module}});
                        I2C_Enable({{module}});{{irq}}
                    }
                }
                """;
        }
        Require("RX0");
        Require("TX0");
        var (prescaler, seg1, seg2) = CanTiming(clock, rate, item.SamplePointPermille!.Value, setup?.Number("CAN0", "SJW") ?? 1);
        var canIrq = item.InterruptMask == 0 ? "" : $"\n    (void)CAN_GetIntStatus({module});\n    {module}->IER = {Ag32BusInterrupts.CanMask(item.InterruptMask)};\n" +
            Ag32InterruptCode.Enable(module, item.InterruptPriority, "    ");
        var mode = setup?.Value("CAN0", "Mode") switch
        {
            "只听" => "CAN_MOD_LISTONLY",
            "自测" => "CAN_MOD_SELFTEST",
            _ => "0u"
        };
        var configure = mode == "0u" ? $"CAN_Init({module}, &config);" : $$"""
                /* 位时序、过滤和监听模式全部设置后一次退出复位。 */
                {{module}}->BTR0 = config.Prescaler | config.SJW;
                {{module}}->BTR1 = config.TSeg1 | config.TSeg2 | config.Sampling;
                for (unsigned i = 0; i < 4; ++i) {
                    {{module}}->AMR[i] = config.FilterConfig.FilterMask[i];
                    {{module}}->ACR[i] = config.FilterConfig.FilterCode[i];
                }
                {{module}}->OCR = CAN_OUTPUT_MODE_NORMAL;
                {{module}}->RXERR = 0;
                {{module}}->TXERR = 0;
                {{module}}->MOD = config.FilterConfig.FilterMode | {{mode}};
                """;
        return $$"""
            {{comment}}
            CAN_EnableResetMode({{module}});
            {{module}}->IER = 0u;
            if (StudioX_PeripheralClockHz() == {{clock}}u)
            {
                CAN_InitTypeDef config;
                CAN_StructInit(&config);{{FilterCode(item, setup)}}
                config.Prescaler = {{prescaler - 1}}u;
                config.TSeg1 = CAN_TSEG1_{{seg1}}TQ;
                config.TSeg2 = CAN_TSEG2_{{seg2}}TQ;
                config.SJW = CAN_SJW_{{setup?.Number("CAN0","SJW") ?? 1}}TQ;
                config.Sampling = CAN_SAMPLING_{{setup?.Value("CAN0","Samples") ?? "1"}};
                {{configure}}{{canIrq}}
            }
            """;
    }
}
