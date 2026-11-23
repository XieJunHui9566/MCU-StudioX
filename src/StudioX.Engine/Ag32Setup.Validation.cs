namespace StudioX.Engine;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

public sealed partial class Ag32Setup
{
    private static readonly Dictionary<string, int> DmaRequests = BuildDmaRequests();
    private static Dictionary<string, int> BuildDmaRequests()
    {
        var requests = new Dictionary<string, int>();
        // AG32 Reference Manual 表 10，复用编号并非独立请求线。
        for (int i = 0; i < 5; i++)
        {
            requests[$"UART{i}_RX"] = (14 + i * 2) % 16;
            requests[$"UART{i}_TX"] = (13 + i * 2) % 16;
            requests[$"GPTIMER{i}_UPDATE"] = (10 + i * 7) % 16;
            requests[$"GPTIMER{i}_COM"] = (15 + i * 7) % 16;
            requests[$"GPTIMER{i}_TRIGGER"] = (16 + i * 7) % 16;
            for (int ch = 0; ch < 4; ch++)

            {
                requests[$"GPTIMER{i}_CC{ch}"] = (11 + i * 7 + ch) % 16;
            }
        }
        for (int i = 0; i < 3; i++)

        {
            requests[$"ADC{i}"] = i + 1;
        }
        requests["DAC0"] = 4;
        requests["DAC1"] = 3;
        requests["MEMORY"] = -1;
        return requests;
    }

    public static int UsbDmPin(string device) => Ag32DeviceCatalog.Require(device).TargetDevice switch
    {
        "AGRV2KQ32" => 22,
        "AGRV2KL48" => 32,
        "AGRV2KL64" or "AGRV2KL64H" => 44,
        "AGRV2KL100" or "AGRV2KL100H" => 70,
        _ => throw Error("USB 封装未核实。")
    };

    public static int RtcOutputPin(string device) => Ag32DeviceCatalog.Require(device).PinCount switch
    {
        32 => 1,
        48 or 64 => 2,
        100 => 7,
        _ => throw Error("当前封装的 RTC 输出脚未核实。")
    };

    public bool UsesComparatorPin(string name)
    {
        // 无实例参数的老工程保留原先全部比较器输入的预留。
        if (!Units.ContainsKey("CMP1") && !Units.ContainsKey("CMP2"))

        {
            return true;
        }
        return new[] { "CMP1", "CMP2" }.Any(c => Units.ContainsKey(c) && ("CMP_" + Value(c, "Plus") == name || "CMP_" + Value(c, "Minus") == name));
    }

    private uint CrcValue(string key)
    {
        var text = Value("CRC0", key);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        if (!uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var word) ||
            (ulong)word >= (1UL << Number("CRC0", "Width")) || key == "Polynomial" && (word & 1) == 0)

        {
            throw Error($"CRC0 的 {key} 必须符合所选位宽，多项式常数项必须为 1。");
        }
        return word;
    }

    public int[] Sequence(string unit)
    {
        var parts = Value(unit, "Sequence").Split([',', '，'], StringSplitOptions.TrimEntries);
        if (parts.Length > 16 || parts.Any(s => !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 0 or > 16))

        {
            throw Error(unit + " 扫描序列需要 1～16 个通道号（0～16）。");
        }
        return parts.Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    }

    public void Validate(string device, IReadOnlyList<Ag32PinAssignment> pins, Ag32AnalogSettings analog,
        Ag32UartSettings[] uarts, Ag32BusSettings[] buses, Ag32TimerSettings[] timers, Ag32SystemPeripherals system, Ag32PinClockSettings clocks)
    {
        CheckFields();
        if (Units.ContainsKey("HyperRAM"))
        {
            if (Ag32DeviceCatalog.Require(device).ExternalPsramBytes != 8 * 1024 * 1024)

            {
                throw Error("当前型号没有封装内 HyperRAM。");
            }
            if (!Ag32PeripheralSupport.HyperRamControllerAvailable)

            {
                throw Error("当前 IDE 缺少 AGM HyperRAM 控制器资源。请使用带此资源的构建，或关闭 HyperRAM 配置。");
            }
            if (clocks.SysMhz is not (> 0 and <= 200) || clocks.HseMhz is not > 0)

            {
                throw Error("HyperRAM 需要显式配置外部时钟及最高 200 MHz 的系统时钟。");
            }
            if (analog.Enabled)

            {
                throw Error("HyperRAM 和模拟 IP 都占用逻辑区的 MCU 存储接口，请选择一个控制器。");
            }
            if (Units.ContainsKey("LOCAL_INT0"))

            {
                throw Error("HyperRAM 控制器使用 LOCAL_INT0 通知 DMA 完成，此中断不能再分配给外部输入。");
            }
        }
        var channels = new Dictionary<int, string>();
        var requests = new Dictionary<int, string>();
        void Claim(int channel, int request, string name)
        {
            if (!channels.TryAdd(channel, name))

            {
                throw Error($"DMA 通道 {channel} 已分给 {channels[channel]}，与 {name} 冲突。");
            }
            if (request >= 0 && !requests.TryAdd(request, name))

            {
                throw Error($"硬件 DMA 请求 {request} 被 {requests[request]} 和 {name} 共用。");
            }
        }
        foreach (var spi in buses.Where(s => s.Module.StartsWith("SPI", StringComparison.Ordinal)))
        {
            var first = spi.Module == "SPI0" ? 6 : 8;
            if (spi.RxDmaChannel is int rx)

            {
                Claim(rx, first + 1, spi.Module + "_RX");
            }
            if (spi.TxDmaChannel is int tx)

            {
                Claim(tx, first, spi.Module + "_TX");
            }
        }
        var analogPins = Ag32PeripheralSupport.Pins(device);
        bool Mapped(string f) => pins.Any(p => p.Function == f);
        void RequirePin(string name)
        {
            if (!Mapped(name))

            {
                throw Error("请先在封装图分配 " + name + "。");
            }
        }
        foreach (var unit in Units.Keys)
        {
            string V(string k) => Value(unit, k);
            if (unit.StartsWith("UART") && !uarts.Any(u => u.Module == unit))

            {
                throw Error("请先启用 " + unit + " 初始化。");
            }
            if (IrqEvents(unit).Length > 0 && (Number(unit, "Events") & ~IrqEvents(unit).Sum(e => e.Mask)) != 0)
            {
                throw Error(unit + " 的中断事件无效。");
            }
            if (unit == "USB0" && (Number(unit, "Events") & ~UsbEvents(V("Role")).Sum(e => e.Mask)) != 0)
            {
                throw Error("当前 USB 角色不支持所选中断事件。");
            }
            if (unit == "RTC" && V("Output") != "关闭" && pins.Any(p => p.PinNumber == RtcOutputPin(device)))

            {
                throw Error($"RTC 输出固定使用 PIN_{RtcOutputPin(device)}，请释放该引脚。");
            }
            if (unit == "RTC" && V("Clock").StartsWith("LOCAL") && !(clocks.HseMhz > 0))

            {
                throw Error("逻辑转接 HSE 需要有效的外部输入时钟。");
            }
            if (unit == "RTC" && system.RtcPrescaler is null)

            {
                throw Error("请先启用 RTC 初始化。");
            }
            if (unit == "CRC0")
            {
                _ = CrcValue("Polynomial");
                _ = CrcValue("Seed");
            }
            if (unit == "BKP" && Number(unit, "Last") < Number(unit, "First"))
            {
                throw Error("备份寄存器范围倒置。");
            }
            if (unit.StartsWith("LOCAL_INT"))

            {
                RequirePin(V("Source"));
            }
            if (unit.StartsWith("EXT_INT"))

            {
                RequirePin(unit);
            }
            if (unit.StartsWith("ADC"))
            {
                decimal mhz = clocks.BusMhz is > 0 ? clocks.BusMhz.Value : clocks.SysMhz ?? 10;
                if (mhz >= 24m * (Number(unit, "Divider") + 1))

                {
                    throw Error($"{unit} 的采样时钟必须低于 12 MHz，请增加分频值。");
                }
                foreach (var n in Sequence(unit))

                {
                    if (!analog.Enabled || (n == 16 ? unit != "ADC1" : (analog.AdcChannels & (1u << n)) == 0))

                    {
                        throw Error($"{unit} 的通道 {n} 需要在模拟外设中预留；内部温度通道 16 仅适用于 ADC1。");
                    }
                }
                if (V("Mode") == "DMA 连续" && !Units.Keys.Any(k => k.StartsWith("DMA_CH") && Value(k, "Request") == unit))

                {
                    throw Error("请为 " + unit + " 选择一个 DMA 通道。");
                }
            }
            if (unit.StartsWith("DAC") && (!analog.Enabled || !(unit == "DAC0" ? analog.Dac0 : analog.Dac1)))

            {
                throw Error("请先在模拟外设中预留 " + unit + " 输出。");
            }
            if (unit.StartsWith("DAC") && V("Mode") == "DMA 输出" && !Units.Keys.Any(k => k.StartsWith("DMA_CH") && Value(k, "Request") == unit))

            {
                throw Error("请为 " + unit + " 选择 DMA 通道。");
            }
            if (unit.StartsWith("CMP"))
            {
                if (!analog.Enabled || !analog.Comparator)

                {
                    throw Error("请先在模拟外设中启用比较器。");
                }
                foreach (var ch in new[] { V("Plus"), V("Minus") }.Where(s => s.StartsWith("PA")))

                {
                    if (!analogPins.Any(p => p.ComparatorInput == "CMP_" + ch))

                    {
                        throw Error($"{device} 没有引出 {unit} 的 {ch} 输入。");
                    }
                }
            }
            if (Ag32TimerInitialization.IsBasic(unit) && !timers.Any(t => t.Module == unit))
            {
                throw Error("请先启用 " + unit + " 的时基。");
            }
            if (unit is "SPI0" or "SPI1")
            {
                if (!buses.Any(b => b.Module == unit))
                {
                    throw Error("请先启用 " + unit + " 初始化。");
                }
                for (var i = 1; i <= Number(unit, "Phases"); i++)
                {
                    var action = V("Action" + i);
                    var width = Number(unit, "Lines" + i);
                    if (action is "TX" or "RX" && Number(unit, "Bytes" + i) > 4)
                    {
                        throw Error($"{unit} 阶段 {i} 寄存器容量为 4 字节，长数据请使用 DMA 接口。");
                    }
                    if (action == "POLL" && (Number(unit, "Expect" + i) & ~Number(unit, "Mask" + i)) != 0)
                    {
                        throw Error("SPI 轮询期望值包含未掩选的位。");
                    }
                    if (action == "POLL" && i != Number(unit, "Phases"))
                    {
                        throw Error("SPI 轮询比较应为最后一个阶段。");
                    }
                    if (width != 1 || action is "TX" or "DUMMY_TX")
                    {
                        RequirePin(unit + "_SI_IO0");
                    }
                    if (width != 1 || action is "RX" or "POLL")
                    {
                        RequirePin(unit + "_SO_IO1");
                    }
                    if (width == 4)
                    {
                        RequirePin(unit + "_WPN_IO2");
                        RequirePin(unit + "_HOLDN_IO3");
                    }
                }
            }
            if (unit == "CAN0" && !buses.Any(b => b.Module == unit))
            {
                throw Error("请先启用 CAN0 初始化。");
            }
            if (unit.StartsWith("GPTIMER"))
            {
                var t = timers.SingleOrDefault(t => t.Module == unit) ?? throw Error("请先启用 " + unit + " 的时基。");
                if (V("Direction") != "向上" && (t.Encoder is not null || V("Pulse") == "单脉冲"))
                {
                    throw Error("编码器和当前单脉冲模式需要选择向上计数。");
                }
                if (t.Encoder is not null && (V("Slave") != "关闭" || V("Pulse") == "单脉冲"))

                {
                    throw Error(unit + " 的编码器占用从模式控制器。");
                }
                if (V("Slave") != "关闭")
                {
                    var input = V("Input");
                    if (input == "ETR")

                    {
                        RequirePin(unit + "_ETR");
                    }
                    else if (input.StartsWith("CH"))
                    {
                        RequirePin(unit + "_" + input);
                        if (t.Channels[input[^1] - '0']?.Edge is null)

                        {
                            throw Error(unit + " 的 " + input + " 需配置为输入捕获。");
                        }
                    }
                    else if (!Units.ContainsKey(input.Split('_')[0]) || Value(input.Split('_')[0], "Output") == "RESET")

                    {
                        throw Error(input + " 的主定时器需要配置持续有效的触发输出。");
                    }
                }
                var output = V("Output");
                if ((output.StartsWith("OC") && t.Channels[output[2] - '0']?.IsOutput != true) ||
                    (output == "CC0IF" && t.Ch0 is null))

                {
                    throw Error(unit + " 主触发输出需启用对应通道。");
                }
            }
            if (unit.StartsWith("DMA_CH"))
            {
                if (V("Layout") == "分段缓冲区")
                {
                    DmaSegmentSizes(unit);
                }
                var q = V("Request");
                Claim(unit[^1] - '0', DmaRequests[q], q);
                var port = q.Split('_')[0];
                if (q.StartsWith("UART"))
                {
                    var u = uarts.SingleOrDefault(u => u.Module == port) ?? throw Error("请先配置 " + port + "。");
                    var rx = q.EndsWith("_RX");
                    RequirePin(port + (rx ? "_UARTRXD" : "_UARTTXD"));
                    if ((rx && (u.Direction == "TX" || u.ReceiveInterrupt || (u.AdditionalInterruptMask & (0x10 | 0x40)) != 0)) ||
                        (!rx && (u.Direction == "RX" || (u.AdditionalInterruptMask & 0x20) != 0)))

                    {
                        throw Error(q + " 与串口方向或接收中断冲突。");
                    }
                }
                if (q.StartsWith("GPTIMER"))
                {
                    var t = timers.SingleOrDefault(t => t.Module == port) ?? throw Error("请先配置 " + port + "。");
                    if (q.Contains("_CC") && t.Channels[q[^1] - '0'] is null)

                    {
                        throw Error(q + " 需要启用对应通道。");
                    }
                }
                if (q.StartsWith("DAC") && (!Units.ContainsKey(q) || Value(q, "Mode") != "DMA 输出"))

                {
                    throw Error(q + " 应选择 DMA 输出。");
                }
                if (q.StartsWith("ADC") && (!Units.ContainsKey(q) || Value(q, "Mode") != "DMA 连续"))

                {
                    throw Error(q + " 应选择 DMA 连续转换。");
                }
            }

            if (unit == "PWR")
            {
                for (int i = 0; i < 8; i++)

                {
                    if (V("EXT" + i) != "关闭")

                    {
                        RequirePin("EXT_INT" + i);
                    }
                }
                if (Value("RTC", "Clock").StartsWith("LOCAL") && (V("Alarm") != "关闭" || V("Standby").Contains("RTC")))
                {
                    throw Error("RTC 自动唤醒需要使用低速 LSE 或 LSI。");
                }
                if ((V("Alarm") != "关闭" || V("Standby").Contains("RTC")) && system.RtcPrescaler is null)

                {
                    throw Error("RTC 闹钟唤醒需要启用 RTC。");
                }
            }
            if (unit == "USB0")
            {
                var dm = UsbDmPin(device);
                if (pins.Any(p => p.PinNumber == dm || p.PinNumber == dm + 1))

                {
                    throw Error($"USB D− / D+ 固定使用 PIN_{dm} / PIN_{dm + 1}，请释放数字分配。");
                }
                if (Mapped("USB0_ID"))

                {
                    throw Error("固定主机或设备角色时请释放 USB0_ID，以免转换器切换到 OTG。");
                }
                if ((V("Clock") == "外部 USB0_CLK") != Mapped("USB0_CLK"))

                {
                    throw Error("USB 时钟选择必须与 USB0_CLK 引脚分配一致。");
                }
                foreach (var ep in EndpointDirections())
                {
                    var type = V("EP" + ep);
                    var size = Number(unit, "Size" + ep);
                    if (V("Role") == "Device" && type != "关闭" && (type == "Bulk" && size is not (8 or 16 or 32 or 64) || type == "Interrupt" && size > 64))

                    {
                        throw Error($"USB EP{ep} 的方向、类型或包长不适用于当前全速角色。");
                    }
                }
            }
            if (unit == "MAC0")
            {
                var address = V("Address");
                if (!Regex.IsMatch(address, @"\A(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\z") || (Convert.ToByte(address[..2], 16) & 1) != 0 || address == "00:00:00:00:00:00")

                {
                    throw Error("MAC0 需要有效的单播 MAC 地址。");
                }
                foreach (var pin in "MDIO MDC RXD0 RXD1 RX_DV TXD0 TXD1 TX_EN".Split())

                {
                    RequirePin("MAC0_" + pin);
                }
                if (V("Mode") == "MII")
                {
                    foreach (var pin in "RXD2 RXD3 TXD2 TXD3 TX_CLK RX_CLK CRS COL RX_ER".Split())

                    {
                        RequirePin("MAC0_" + pin);
                    }
                }
                else if (Mapped("MAC0_RXD2") || Mapped("MAC0_TXD2") || Mapped("MAC0_RXD3") || Mapped("MAC0_TXD3"))

                {
                    throw Error("RMII 只使用两位收发数据线。");
                }
                if (V("Ref") == "MCU 输出")

                {
                    RequirePin("MAC0_CLK_OUT");
                }
                else if (Mapped("MAC0_CLK_OUT") || (!Mapped("MAC0_TX_CLK") && !Mapped("MAC0_RX_CLK")))

                {
                    throw Error("PHY 输入需映射参考时钟并释放 MAC0_CLK_OUT。");
                }
            }
        }
        _ = OrderTimers(timers);
    }

    internal void CheckUsbDirective(byte[] bytes)
    {
        if (!Units.ContainsKey("USB0"))

        {
            return;
        }
        var lines = UsbLine.Matches(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        if (lines.Count != 1 || !lines[0].Groups[1].Value.Equals(Value("USB0", "Role"), StringComparison.OrdinalIgnoreCase))

        {
            throw Error("USB0 固件与 VE 的角色不一致。");
        }
    }

    internal static IEnumerable<string> EndpointDirections() =>
        Enumerable.Range(1, 3).SelectMany(ep => new[] { ep + "OUT", ep + "IN" });

    internal Ag32TimerSettings[] OrderTimers(Ag32TimerSettings[] timers)
    {
        var pending = timers.ToList();
        var result = new List<Ag32TimerSettings>();
        while (pending.Count != 0)
        {
            var timer = pending.FirstOrDefault(t => !pending.Any(child => Ag32TimerInitialization.IsGeneral(child.Module) && Units.ContainsKey(child.Module) &&
                Value(child.Module, "Slave") != "关闭" && Value(child.Module, "Input") == t.Module + "_TRGO"));
            if (timer is null)

            {
                throw Error("GPTIMER 主从触发形成循环。");
            }
            result.Add(timer);
            pending.Remove(timer);
        }
        return result.ToArray();
    }

    internal IEnumerable<string> TimerStart(string module)
    {
        string V(string field) => Value(module, field);
        if (V("Pulse") == "单脉冲")

        {
            yield return $"GPTIMER_SetOnePulseMode({module}, GPTIMER_ONEPULSEMODE_SINGLE);";
        }
        yield return $"GPTIMER_SetTriggerOutput({module}, GPTIMER_TRGO_{V("Output")});";
        if (V("Slave") != "关闭")
        {
            var input = V("Input");
            if (input == "ETR")

            {
                yield return $"GPTIMER_ConfigETR({module}, GPTIMER_ETR_POLARITY_{(V("EtrEdge") == "反相" ? "INVERTED" : "NONINVERTED")}, GPTIMER_ETR_PRESCALER_DIV{V("EtrDiv")}, (GPTIMER_ETR_FilterTypeDef)({V("EtrFilter")}u << GPTIMER_SMCR_ETF_OFFSET));";
            }
            var ts = input switch
            {
                "CH0" => "TI0FP0",
                "CH1" => "TI1FP1",
                "ETR" => "ETRF",
                _ => "ITR" + ((input[7] - module[7] + 4) % 5)
            };
            var mode = V("Slave") switch
            {
                "触发启动" => "TRIGGER",
                "门控" => "GATED",
                _ => "RESET"
            };
            yield return $"GPTIMER_SetTriggerInput({module}, GPTIMER_TS_{ts});";
            yield return $"GPTIMER_SetSlaveMode({module}, GPTIMER_SLAVEMODE_{mode});";
        }
        if (V("Slave") != "触发启动")

        {
            yield return $"GPTIMER_EnableCounter({module});";
        }
    }
}
