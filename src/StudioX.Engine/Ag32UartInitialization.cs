namespace StudioX.Engine;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public static class Ag32UartInitialization
{
    public static IReadOnlyList<Ag32BusInterruptEvent> AdditionalEvents
    {
        get;
    } = [
        new(1 << 5, "发送 FIFO 就绪", "UART_INT_TX"), new(1 << 1, "CTS 变化", "UART_INT_CTSM"),
        new(1 << 7, "帧错误", "UART_INT_FE"), new(1 << 8, "奇偶校验错误", "UART_INT_PE"),
        new(1 << 9, "Break 检测", "UART_INT_BE"), new(1 << 10, "接收溢出", "UART_INT_OE")];
    private static string InterruptFlags(Ag32UartSettings item)
    {
        var flags = AdditionalEvents.Where(e => (item.AdditionalInterruptMask & e.Mask) != 0).Select(e => e.Flag).ToList();
        if (item.ReceiveInterrupt)
        {
            flags.Insert(0, "UART_INT_RX | UART_INT_RT");
        }
        return string.Join(" | ", flags);
    }
    private const string Marker = "#@StudioX:UART";
    private static readonly Regex Lines = new(@"(?<![^\r\n])[ \t]*#@StudioX:UART[^\r\n]*(?:\r\n|[\r\n]|\z)", RegexOptions.CultureInvariant);

    internal static Ag32UartSettings[] Read(byte[] source)
    {
        var text = Encoding.UTF8.GetString(source).TrimStart('\uFEFF');
        var result = new List<Ag32UartSettings>();
        foreach (Match line in Lines.Matches(text))
        {
            var match = Regex.Match(line.Value.Trim(), @"\A#@StudioX:UART ([0-4]) baud=([0-9]+) rxirq=([01])(?: data=([5-8]) parity=(NONE|EVEN|ODD|ONE|ZERO) stop=([12]) fifo=([01]) flow=(NONE|RTS|CTS|RTS_CTS) priority=([1-9]|1[0-5])(?: mode=(AUTO|TX|RX|TX_RX))?(?: irq=([0-9]{1,4}))?)?\z");
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out var baud))

            {
                throw new StudioXException("AG32_UART_CONFIG", "无效的 UART 配置行：" + line.Value.Trim());
            }
            var port = match.Groups[1].Value[0] - '0';
            if (result.Any(item => item.Port == port))

            {
                throw new StudioXException("AG32_UART_CONFIG", $"UART{port} 的初始化配置重复。");
            }
            var item = new Ag32UartSettings(port, baud, match.Groups[3].Value == "1");
            if (match.Groups[4].Success)

            {
                item = item with
                {
                    DataBits = int.Parse(match.Groups[4].Value),
                    Parity = match.Groups[5].Value,
                    StopBits = int.Parse(match.Groups[6].Value),
                    Fifo = match.Groups[7].Value == "1",
                    FlowControl = match.Groups[8].Value,
                    InterruptPriority = int.Parse(match.Groups[9].Value),
                    Direction = match.Groups[10].Success ? match.Groups[10].Value : "AUTO",
                    AdditionalInterruptMask = match.Groups[11].Success ? int.Parse(match.Groups[11].Value, CultureInfo.InvariantCulture) : 0
                };
            }
            ValidateFormat(item);
            result.Add(item);
        }
        return result.OrderBy(item => item.Port).ToArray();
    }

    internal static byte[] Write(byte[] source, IReadOnlyList<Ag32UartSettings>? settings)
    {
        if (settings is null || Read(source).SequenceEqual(settings.OrderBy(item => item.Port)))

        {
            return source;
        }
        var bom = source is [0xef, 0xbb, 0xbf, ..];
        var text = Encoding.UTF8.GetString(bom ? source[3..] : source);
        var ending = Regex.Match(text, @"\r\n|[\r\n]") is { Success: true } first ? first.Value : "\n";
        var result = new StringBuilder(Lines.Replace(text, ""));
        foreach (var item in settings.OrderBy(item => item.Port))
        {
            if (result.Length != 0 && result[^1] is not ('\n' or '\r'))
            {
                result.Append(ending);
            }
            ValidateFormat(item);
            result.Append(CultureInfo.InvariantCulture, $"{Marker} {item.Port} baud={item.Baud} rxirq={(item.ReceiveInterrupt ? 1 : 0)}");
            if (item != new Ag32UartSettings(item.Port, item.Baud, item.ReceiveInterrupt))

            {
                result.Append(CultureInfo.InvariantCulture, $" data={item.DataBits} parity={item.Parity} stop={item.StopBits} fifo={(item.Fifo ? 1 : 0)} flow={item.FlowControl} priority={item.InterruptPriority}");
            }
            if (item.Direction != "AUTO")
            {
                result.Append(" mode=").Append(item.Direction);
            }
            if (item.AdditionalInterruptMask != 0)
            {
                result.Append(" irq=").Append(item.AdditionalInterruptMask);
            }
            result.Append(ending);
        }
        var bytes = Encoding.UTF8.GetBytes(result.ToString());
        return bom ? [0xef, 0xbb, 0xbf, .. bytes] : bytes;
    }

    private static void ValidateFormat(Ag32UartSettings item)
    {
        if (item.Port is < 0 or > 4)
        {
            throw new StudioXException("AG32_UART_CONFIG", "UART 实例超出范围。");
        }
        if ((item.AdditionalInterruptMask & ~AdditionalEvents.Sum(e => e.Mask)) != 0 ||
            item.DataBits is < 5 or > 8 || item.StopBits is not (1 or 2) ||
            item.Parity is not ("NONE" or "ODD" or "EVEN" or "ONE" or "ZERO") ||
            item.FlowControl is not ("NONE" or "RTS" or "CTS" or "RTS_CTS") || item.InterruptPriority is < 1 or > 15 ||
            item.Direction is not ("AUTO" or "TX" or "RX" or "TX_RX"))

        {
            throw new StudioXException("AG32_UART_CONFIG", $"{item.Module} 的帧格式、硬件流控或中断优先级无效。");
        }
    }

    public static string InitCode(Ag32UartSettings item, IReadOnlyList<Ag32PinAssignment> pins, decimal clockHz, Ag32Setup? setup = null)
    {
        ValidateFormat(item);
        var uart = item.Module;
        var loopback = setup?.Value(uart, "Loop") == "开启";
        var tx = loopback || pins.Any(pin => pin.Function == uart + "_UARTTXD");
        var rx = loopback || pins.Any(pin => pin.Function == uart + "_UARTRXD");
        if (item.Direction is "TX" or "TX_RX" && !tx || item.Direction is "RX" or "TX_RX" && !rx)

        {
            throw new StudioXException("AG32_UART_PIN", $"{uart} 的收发方向与引脚不符，请分配该方向所需的 TX / RX 引脚。");
        }
        tx &= item.Direction != "RX";
        rx &= item.Direction != "TX";
        if (!tx && !rx)
        {
            throw new StudioXException("AG32_UART_PIN", $"{uart} 请至少选择一个 TX 或 RX 引脚。");
        }
        if (item.ReceiveInterrupt && !rx)
        {
            throw new StudioXException("AG32_UART_PIN", $"{uart} 接收中断需要启用接收方向并分配 RX 引脚。");
        }
        if ((item.AdditionalInterruptMask & 0x780) != 0 && !rx)

        {
            throw new StudioXException("AG32_UART_PIN", $"{uart} 接收错误中断需要启用接收方向并分配 RX 引脚。");
        }
        if ((item.AdditionalInterruptMask & (1 << 5)) != 0 && !tx)

        {
            throw new StudioXException("AG32_UART_PIN", $"{uart} 发送中断需要启用发送方向并分配 TX 引脚。");
        }
        if ((item.AdditionalInterruptMask & (1 << 1)) != 0 && !pins.Any(p => p.Function == uart + "_NUARTCTS"))

        {
            throw new StudioXException("AG32_UART_PIN", $"{uart} CTS 变化中断需要分配 CTS 引脚。");
        }
        var rts = item.FlowControl is "RTS" or "RTS_CTS";
        var cts = item.FlowControl is "CTS" or "RTS_CTS";
        if (rts && (!rx || !pins.Any(pin => pin.Function == uart + "_NUARTRTS")))

        {
            throw new StudioXException("AG32_UART_PIN", $"{uart} 使用 RTS 流控需要分配 RX 和 RTS 引脚。");
        }
        if (cts && (!tx || !pins.Any(pin => pin.Function == uart + "_NUARTCTS")))

        {
            throw new StudioXException("AG32_UART_PIN", $"{uart} 使用 CTS 流控需要分配 TX 和 CTS 引脚。");
        }
        var max = decimal.Floor(clockHz / 16m);
        var min = Math.Max(1m, decimal.Ceiling(clockHz / (16m * 65535m)));
        if (item.Baud < min || item.Baud > max)

        {
            throw new StudioXException("AG32_UART_BAUD", $"{uart} 当前外设时钟 {clockHz / 1000000m:0.######} MHz 可设 {min}–{max} bit/s。");
        }
        var code = new StringBuilder();
        var direction = tx && rx ? "收发" : tx ? "只发送" : "只接收";
        var parity = item.Parity switch
        {
            "EVEN" => "偶校验",
            "ODD" => "奇校验",
            "ONE" => "校验位恒为 1",
            "ZERO" => "校验位恒为 0",
            _ => "无校验"
        };
        code.AppendLine($"/* {uart} {direction}，{item.Baud} bit/s，{item.DataBits} 位数据，{parity}，{item.StopBits} 位停止位。 */");
        // PLL 启动失败时系统会回退到 HSI；不能在实际时钟下写入不可表示的分频值。
        code.AppendLine($"if (StudioX_PeripheralClockHz() >= {item.Baud * 16L}u &&")
            .AppendLine($"    StudioX_PeripheralClockHz() <= {Math.Min(uint.MaxValue, item.Baud * 16L * 65535)}u)")
            .AppendLine("{")
            .AppendLine($"    UART_Init({uart}, (UART_BaudRateTypeDef){item.Baud}, UART_LCR_DATABITS_{item.DataBits},")
            .AppendLine($"        UART_LCR_STOPBITS_{item.StopBits}, UART_LCR_PARITY_{item.Parity}, UART_LCR_FIFO_{(item.Fifo ? 16 : 1)});");
        if (setup?.Units.ContainsKey(uart) == true)
        {
            string Level(string field) => setup.Value(uart, field) switch
            {
                "1/8" => "1_8TH",
                "1/4" => "1_4TH",
                "3/4" => "3_4TH",
                "7/8" => "7_8TH",
                _ => "HALF"
            };
            if (item.Fifo)

            {
                code.AppendLine($"    UART_SetRxIntFifoLevel({uart}, UART_INT_FIFO_{Level("RxLevel")});")
                                    .AppendLine($"    UART_SetTxIntFifoLevel({uart}, UART_INT_FIFO_{Level("TxLevel")});");
            }
            code.AppendLine($"    UART_{(setup.Value(uart,"Loop") == "开启" ? "Enable" : "Disable")}LoopBack({uart});");
        }
        if (!tx || !rx)
        {
            code.AppendLine($"    {uart}->CR &= ~UART_CR_{(tx ? "RXE" : "TXE")};");
        }
        if (rts)
        {
            code.AppendLine($"    UART_EnableRTS({uart});");
        }
        if (cts)
        {
            code.AppendLine($"    UART_EnableCTS({uart});");
        }
        if (item.ReceiveInterrupt || item.AdditionalInterruptMask != 0)
        {
            code.AppendLine($"    UART_EnableInt({uart}, {InterruptFlags(item)});")
                .AppendLine(Ag32InterruptCode.Enable(uart, item.InterruptPriority, "    "));
        }
        return code.Append('}').ToString();
    }

    internal static void Generate(byte[] ve, Ag32PinAssignment[] pins, uint clockHz,
        StringBuilder declarations, StringBuilder definitions, StringBuilder init)
    {
        var setup = Ag32Setup.Read(ve);
        foreach (var item in Read(ve))
        {
            var code = InitCode(item, pins, clockHz, setup);
            foreach (var line in code.Split('\n'))
            {
                if (!line.StartsWith('#'))
                {
                    init.Append("    ");
                }
                init.AppendLine(line.TrimEnd('\r'));
            }
            if (!item.ReceiveInterrupt && item.AdditionalInterruptMask == 0)
            {
                continue;
            }
            var name = "StudioX_" + item.Module;
            if (item.AdditionalInterruptMask != 0)
            {
                declarations.AppendLine("/* events 为 UART_INT_* 位。发送就绪按次通知，有数据时重新使能 UART_INT_TX。 */");
                Ag32InterruptCode.Callback(declarations, definitions, $"void SX_{item.Module}_Interrupt(uint32_t events)",
                    $"{item.Module} 中断。events 中置位的 UART_INT_* 表示本次触发的事件。", "(void)events;");
            }
            if (item.ReceiveInterrupt)
            {
                declarations.AppendLine($"bool {name}_TryRead(uint8_t *value);")
                    .AppendLine($"extern volatile uint32_t {name}_Dropped;");
                definitions.AppendLine($$"""
                    /* 单生产者 ISR、单消费者。队列已满时保留旧数据并累计溢出。 */
                    static volatile uint8_t {{name}}_Buffer[64];
                    static volatile uint8_t {{name}}_Head, {{name}}_Tail;
                    volatile uint32_t {{name}}_Dropped;

                    static void {{name}}_DrainRx(void)
                    {
                        for (unsigned int i = 0; i < 16u && !UART_IsRxFifoEmpty({{item.Module}}); ++i)
                        {
                            uint8_t value = UART_ReceiveData({{item.Module}});
                            uint8_t head = {{name}}_Head;
                            uint8_t next = (head + 1u) & 63u;
                            if (next == {{name}}_Tail) { ++{{name}}_Dropped; }
                            else { {{name}}_Buffer[head] = value; {{name}}_Head = next; }
                        }
                    }

                    bool {{name}}_TryRead(uint8_t *value)
                    {
                        uint8_t tail = {{name}}_Tail;
                        if (value == NULL || tail == {{name}}_Head) return false;
                        *value = {{name}}_Buffer[tail];
                        {{name}}_Tail = (tail + 1u) & 63u;
                        return true;
                    }
                    """);
            }
            var uart = item.Module;
            if (item.ReceiveInterrupt)

            {
                definitions.AppendLine($"/* {uart} 的接收和接收超时中断将数据放入队列，由应用调用 {name}_TryRead() 取出。 */");
            }
            definitions.AppendLine($"__attribute__((weak)) void {uart}_isr(void)").AppendLine("{")
                .AppendLine($"    uint32_t events = {uart}->MIS & ({InterruptFlags(item)});");
            if ((item.AdditionalInterruptMask & (1 << 5)) != 0)

            {
                definitions.AppendLine($"    if (events & UART_INT_TX) UART_DisableInt({uart}, UART_INT_TX);");
            }
            definitions.AppendLine($"    UART_ClearInt({uart}, events);");
            if (item.ReceiveInterrupt)

            {
                definitions.AppendLine($"    if (events & (UART_INT_RX | UART_INT_RT)) {name}_DrainRx();");
            }
            if (item.AdditionalInterruptMask != 0)

            {
                definitions.AppendLine($"    if (events) SX_{uart}_Interrupt(events);");
            }
            definitions.AppendLine("}");
        }
    }
}
