namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class Ag32PinPlanningView
{
    private const string HyperRamModule = "HyperRAM";
    private Ag32Setup setupDraft = new();
    private bool buildingSetup;

    private bool ShowSetup(string? unit)
    {
        SetupEnabled.Visibility = Visibility.Visible;
        if (unit is null || !Ag32Setup.Catalog.TryGetValue(unit, out var fields) || Snapshot is null)
        {
            SetupInspector.Visibility = Visibility.Collapsed;
            return false;
        }
        buildingSetup = true;
        try
        {
            SetupInspector.Visibility = Visibility.Visible;
            SetupFields.Children.Clear();
            var timer = Ag32TimerInitialization.IsGeneral(unit);
            SetupEnabled.Content = new TextBlock { Text = timer || unit is "RTC" or "CAN0" || unit.StartsWith("UART") || unit.StartsWith("SPI") || unit.StartsWith("TIMER") ? "配置高级参数" : $"启用 {unit}" };
            SetupEnabled.IsChecked = setupDraft.Units.ContainsKey(unit);
            var missingController = unit == HyperRamModule && !Ag32PeripheralSupport.HyperRamControllerAvailable;
            SetupEnabled.IsEnabled = !missingController || SetupEnabled.IsChecked == true;
            if (missingController)
            {
                SetupHint.Text = "当前 IDE 缺少 AGM HyperRAM 控制器资源。可关闭已有的 HyperRAM 配置。";
                return true;
            }
            var hint = unit switch
            {
                HyperRamModule => $"封装内 8 MiB，地址 0x60000000–0x607FFFFF。板上须短接 {(Snapshot.TargetDevice == "AGRV2KL64H" ? "PIN_8 与 PIN_22" : "PIN_15 与 PIN_35")}（RWDS）。\nHyperBus 跟随系统时钟，参考时钟相移 90°，初始延迟 7 周期。8 MHz 晶振可用下方的 80 / 40 MHz 组合。保存后编译，查看当前工程的时序余量。",
                "DMA_IRQ" => "所有使用中断通知的 DMA 通道共享完成向量和错误向量。",
                "FLASH" => "在“中断 / PLIC”选择操作完成或错误。应用主动解锁后调用 SX_FLASH_ArmInterrupts()。",
                "FCB0" => "在“中断 / PLIC”选择逻辑配置桥错误。启动时保留现有逻辑。",
                _ when unit.StartsWith("SPI") => "每阶段可配置 1 / 2 / 4 线。TX、RX 各容纳 1～4 字节，POLL 为最后一段，超时由调用者指定。",
                "CAN0" => "双组支持两个 ID。扩展帧由硬件预筛高 16 位，回调按完整 29 位判断。",
                _ when unit.StartsWith("TIMER") => "自由运行在所选位宽的计数器回卷时触发，实际周期见时基预览。",
                "CRC0" => "多项式和种子按所选宽度输入十六进制值。初始化后可用 SDK 的 CRC_FeedData 系列接口送入数据。",
                "BKP" => "从 DR0 到 DR15 共 16 个 16 位寄存器。初始化保留备份数据；应用通过 SX_BKP_Read / Write 访问所选范围。",
                "RTC" => $"输出启用后占用 PIN_{Ag32Setup.RtcOutputPin(Snapshot.DeviceId)}。已运行的备份域切换时钟源需由应用处理；SX_RTC_IsReady() 可检查结果。",
                _ when unit.StartsWith("UART") => "FIFO 使能时水位生效。硬件回环将发送数据送回本实例接收端。",
                "IWDG" => "12 位计数器固定从 4095 重装。启动后由应用定期调用 SX_IWDG_Reload()。",
                "PWR" => "Standby 唤醒后重新启动。选含 RTC 的策略会在入睡前设置新闹钟；另一项保留现有闹钟。",
                "USB0" => $"全速 USB · D− PIN_{Ag32Setup.UsbDmPin(Snapshot.DeviceId)}、D+ PIN_{Ag32Setup.UsbDmPin(Snapshot.DeviceId) + 1}。设备 EP0 为 64 字节。主机模式需板级 VBUS 供电。",
                "MAC0" when setupDraft.Value(unit, "Link") == "自动协商" => "应用在主循环中调用 SX_ETH_RefreshLink()，连线后自动选择双方支持的速率和双工。",
                "MAC0" => setupDraft.Value(unit, "Mode") == "RMII" ? "RMII 参考时钟为 50 MHz。PHY 与对端应使用相同的速率和双工配置。" : "MII 在 100 / 10 Mbit/s 时接收与发送时钟分别为 25 / 2.5 MHz。",
                _ when unit.StartsWith("LOCAL_INT") => "引脚经逻辑直达内核，优先级固定。默认回调屏蔽本源；用户回调应解除有效电平。",
                _ when unit.StartsWith("EXT_INT") => "低电平有效，经 PLIC 分发。可在“中断 / PLIC”中定位回调。",
                _ when unit.StartsWith("DMA_CH") && setupDraft.Value(unit, "Layout") == "分段缓冲区" =>
                    $"应用通过 SX_{unit}_StartSegments 按顺序传入各段地址，整链完成后再复用缓冲区。",
                _ when unit.StartsWith("DMA_CH") => $"应用通过 SX_{unit}_Start 传入地址和元素数。突发长度按两端的传输能力选择。",
                _ when unit.StartsWith("ADC") => "外部通道须在模拟引脚页预留。每个序列最多 16 项；ADC1 的通道 16 为内部温度。",
                _ when unit.StartsWith("DAC") => "请选择已预留的 DAC 输出。软件启动使用 SX_" + unit + "_Start()。",
                _ when unit.StartsWith("CMP") => "正反相输入按本封装实际引出的模拟脚选择。",
                _ => "单脉冲模式在一个周期结束时停止。PWM 输出先延迟，再输出占空比指定的有效脉宽。"
            };
            SetupHint.Text = hint;
            if (SetupEnabled.IsChecked != true)

            {
                return true;
            }
            foreach (var f in fields)
            {
                if (unit.StartsWith("SPI") && (f.Key.StartsWith("Mask") || f.Key.StartsWith("Expect") || f.Key.StartsWith("Limit")) &&
                    setupDraft.Value(unit, "Action" + f.Key[^1]) != "POLL")
                {
                    continue;
                }
                if (unit.StartsWith("SPI") && char.IsDigit(f.Key[^1]) &&
                    (f.Key[^1] - '0' > setupDraft.Number(unit, "Phases") || f.Key.StartsWith("Bytes") && setupDraft.Value(unit, "Action" + f.Key[^1]) == "POLL"))
                {
                    continue;
                }
                if (unit.StartsWith("UART") && f.Key.EndsWith("Level") && uartDrafts.TryGetValue(SelectedPort, out var uartDraft) && !uartDraft.Settings.Fifo)
                {
                    continue;
                }
                if (unit.StartsWith("DMA_CH") && (f.Key == "Width" && setupDraft.Value(unit, "Request") != "MEMORY" ||
                    f.Key == "SegmentSizes" && setupDraft.Value(unit, "Layout") != "分段缓冲区" ||
                    f.Key == "Blocks" && setupDraft.Value(unit, "Layout") == "分段缓冲区" ||
                    f.Key == "Target" && !setupDraft.Value(unit, "Request").EndsWith("_TRIGGER") && !setupDraft.Value(unit, "Request").EndsWith("_COM")))
                {
                    continue;
                }
                if (Ag32Setup.IrqEvents(unit).Length != 0 && f.Key is "Events" or "Priority")
                {
                    continue;
                }
                if (unit == "CAN0" && f.Key.StartsWith("Second") && setupDraft.Value(unit, "Filter") != "双组")
                {
                    continue;
                }
                if (unit == "MAC0" && f.Key is "Speed" or "Duplex" && setupDraft.Value(unit, "Link") == "自动协商")
                {
                    continue;
                }
                if (Ag32TimerInitialization.IsBasic(unit) && f.Key == "FreeDiv" && setupDraft.Value(unit, "Mode") != "自由运行")
                {
                    continue;
                }
                if (timer && f.Key.StartsWith("Etr") && (setupDraft.Value(unit, "Slave") == "关闭" || setupDraft.Value(unit, "Input") != "ETR"))
                {
                    continue;
                }
                if (timer && timerDrafts.TryGetValue(unit, out var td) &&
                    (f.Key == "BreakFilter" && td.Brake == "OFF" || f.Key.StartsWith("Pol") && !td.Channels[f.Key[^1] - '0'].IsOutput ||
                     f.Key.StartsWith("NPol") && !td.Channels[f.Key[^1] - '0'].Complementary))
                {
                    continue;
                }
                string value = setupDraft.Value(unit, f.Key);
                if (unit == "USB0" && (f.Key.StartsWith("EP") || f.Key.StartsWith("Size")) &&
                    (setupDraft.Value(unit, "Role") == "Host" || f.Key.StartsWith("Size") && setupDraft.Value(unit, "EP" + f.Key[4..]) == "关闭"))

                {
                    continue;
                }
                if (unit.StartsWith("DAC") && f.Key == "Divider" && setupDraft.Value(unit, "Mode") != "DMA 输出")

                {
                    continue;
                }
                if (unit == "PWR" && f.Key == "AlarmDelay" && !setupDraft.Value(unit, "Standby").Contains("RTC"))

                {
                    continue;
                }
                if (timer && f.Key == "Input" && setupDraft.Value(unit, "Slave") == "关闭")

                {
                    continue;
                }
                Control control;
                if (f.Choices is { } choices)
                {
                    if (unit.StartsWith("CMP"))

                    {
                        choices = choices.Where(c => !c.StartsWith("PA") || c == value || analogPinout.Any(p => p.ComparatorInput == "CMP_" + c)).ToArray();
                    }
                    var picker = new ComboBox { ItemsSource = choices, SelectedItem = value };
                    picker.SelectionChanged += (_, _) =>
                    {
                        if (buildingSetup || loading || busy || picker.SelectedItem is not string v)

                        {
                            return;
                        }
                        if (unit == "CRC0" && f.Key == "Width" && v != value)
                        {
                            string Poly(string w) => w switch
                            {
                                "7" => "09",
                                "8" => "07",
                                "16" => "1021",
                                _ => "04C11DB7"
                            };
                            string Seed(string w) => w switch
                            {
                                "7" => "7F",
                                "8" => "FF",
                                "16" => "FFFF",
                                _ => "FFFFFFFF"
                            };
                            foreach (var (key, oldDefault, newDefault) in new[] { ("Polynomial", Poly(value), Poly(v)), ("Seed", Seed(value), Seed(v)) })

                            {
                                if (setupDraft.Value(unit, key) == oldDefault)
                                {
                                    setupDraft.Units[unit][key] = newDefault;
                                }
                            }
                        }
                        setupDraft.Units[unit][f.Key] = v;
                        MarkChanged();
                        if (unit == "USB0" && f.Key == "Role")

                        {
                            setupDraft.Units[unit]["Events"] = (setupDraft.Number(unit, "Events") & Ag32Setup.UsbEvents(v).Sum(e => e.Mask)).ToString();
                        }
                        ShowSetup(unit);
                        UpdateDiagram();
                    };
                    control = picker;
                }
                else
                {
                    var box = new TextBox { Text = value };
                    box.TextChanged += (_, _) =>
                    {
                        if (buildingSetup || loading || busy)

                        {
                            return;
                        }
                        setupDraft.Units[unit][f.Key] = box.Text;
                        MarkChanged();
                    };
                    control = box;
                }
                AddSetupField(f.Label, control);
                if (f.Key == "SegmentSizes" && control is TextBox segmentBox)
                {
                    var usage = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 6) };
                    void UpdateUsage()
                    {
                        try
                        {
                            var (segments, elements, descriptors) = setupDraft.GetDmaSegmentUsage(unit);
                            usage.Text = $"{segments} 段，共 {elements} 个元素，占用 {descriptors} / 32 个描述符。";
                        }
                        catch (StudioXException ex)
                        {
                            usage.Text = ex.Message;
                        }
                    }
                    UpdateUsage();
                    segmentBox.TextChanged += (_, _) => UpdateUsage();
                    SetupFields.Children.Add(usage);
                }
            }
            if (unit == HyperRamModule)
            {
                var preset = new Button
                {
                    Content = "使用 SYS 80 / BUS 40 MHz",
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new(0, 6, 0, 0),
                    ToolTip = "适用于外部 8 MHz 晶振。保存后编译可查看当前引脚组合的实际时序。"
                };
                preset.IsEnabled = decimal.TryParse(HseClock.Text, out var hse) && hse == 8;
                preset.Click += (_, _) =>
                {
                    SysClock.Text = "80";
                    BusClock.Text = "40";
                    MarkChanged();
                };
                SetupFields.Children.Add(preset);
            }
            if (unit.StartsWith("ADC") || unit.StartsWith("DAC") || unit.StartsWith("CMP"))
            {
                var reserve = new Button { Content = "预留当前模拟输入 / 输出", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 0, 0) };
                reserve.Click += (_, _) => ReserveSetupAnalog(unit);
                SetupFields.Children.Add(reserve);
            }
            if (Ag32Setup.IrqEvents(unit).Length > 0 || unit.StartsWith("DMA_CH"))
            {
                var interrupts = new Button { Content = "配置中断 / PLIC", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 6, 0, 0) };
                interrupts.Click += OpenInterrupts_Click;
                SetupFields.Children.Add(interrupts);
            }
            var signals = unit switch
            {
                "MAC0" => MacSignals(),
                "USB0" => assignments.Where(p => p.Function is "USB0_ID" or "USB0_CLK").Select(p => p.Function)
                    .Union(setupDraft.Value(unit, "Clock") == "外部 USB0_CLK" ? ["USB0_CLK"] : Array.Empty<string>()).ToArray(),
                _ when unit.StartsWith("LOCAL_INT") => [setupDraft.Value(unit, "Source")],
                _ when unit.StartsWith("EXT_INT") => [unit],
                _ when timer && setupDraft.Value(unit, "Slave") != "关闭" && setupDraft.Value(unit, "Input") == "ETR" => [unit + "_ETR"],
                _ => Array.Empty<string>()
            };
            var reserved = new HashSet<int>();
            try
            {
                reserved.UnionWith(Ag32PinPlanningService.GetReservedAnalogPins(Snapshot.DeviceId, GetAnalog(), setupDraft).Select(p => p.Pin));
            }
            catch (StudioXException)
            { /* 保留可修复的旧模拟草稿。 */
            }
            if (setupDraft.Units.ContainsKey("USB0"))
            {
                var dm = Ag32Setup.UsbDmPin(Snapshot.DeviceId);
                reserved.Add(dm);
                reserved.Add(dm + 1);
            }
            foreach (var f in signals)
            {
                var picker = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Number" };
                BindSignalPin(picker, f, reserved);
                picker.SelectionChanged += (_, _) =>
                {
                    if (buildingSetup || busy || loading || picker.SelectedValue is not int n)

                    {
                        return;
                    }
                    AssignPeripheralPin(f, n);
                    ShowSetup(unit);
                };
                AddSetupField(f, picker);
            }
            return true;
        }
        finally
        {
            buildingSetup = false;
        }
    }

    private string[] MacSignals()
    {
        var mii = setupDraft.Value("MAC0", "Mode") == "MII";
        var pins = "MDIO MDC RXD0 RXD1 RX_DV TXD0 TXD1 TX_EN".Split().ToList();
        if (mii)

        {
            pins.AddRange("RXD2 RXD3 TXD2 TXD3 TX_CLK RX_CLK CRS COL RX_ER".Split());
        }
        if (setupDraft.Value("MAC0", "Ref") == "MCU 输出")

        {
            pins.Add("CLK_OUT");
        }
        else if (!mii)

        {
            pins.Add("RX_CLK");
        }
        return pins.Select(p => "MAC0_" + p)
            .Union(assignments.Where(p => p.Function.StartsWith("MAC0_", StringComparison.Ordinal)).Select(p => p.Function))
            .ToArray();
    }

    private string? UsbPad(int number)
    {
        if (Snapshot is not null && setupDraft.Value("RTC", "Output") != "关闭" && number == Ag32Setup.RtcOutputPin(Snapshot.DeviceId))

        {
            return "RTC_OUT";
        }
        if (Snapshot is null || !setupDraft.Units.ContainsKey("USB0"))

        {
            return null;
        }
        return (number - Ag32Setup.UsbDmPin(Snapshot.DeviceId)) switch
        {
            0 => "USB_DM",
            1 => "USB_DP",
            _ => null
        };
    }

    private void AddSetupField(string label, Control control)
    {
        SetupFields.Children.Add(new TextBlock { Text = label, Margin = new(0, 5, 0, 2), TextWrapping = TextWrapping.Wrap });
        AutomationProperties.SetName(control, label);
        SetupFields.Children.Add(control);
    }

    private void ReserveSetupAnalog(string unit)
    {
        try
        {
            var channels = unit.StartsWith("ADC") ? setupDraft.Sequence(unit).Where(n => n != 16).ToArray() : [];
            if (channels.Any(n => !analogPinout.Any(p => p.AdcChannel == n)) ||
                unit == "DAC0" && AnalogDac0.Tag is null || unit == "DAC1" && AnalogDac1.Tag is null)

            {
                throw new StudioXException("AG32_ANALOG_CHANNEL", "当前封装没有引出所选通道。");
            }
            AnalogEnabled.IsChecked = true;
            foreach (CheckBox choice in AnalogChannels.Children)

            {
                if (channels.Contains((int)choice.Tag))

                {
                    choice.IsChecked = true;
                }
            }
            if (unit == "DAC0")

            {
                AnalogDac0.IsChecked = true;
            }
            if (unit == "DAC1")

            {
                AnalogDac1.IsChecked = true;
            }
            if (unit.StartsWith("CMP"))

            {
                AnalogComparator.IsChecked = true;
            }
            MarkChanged();
            UpdateDiagram();
        }
        catch (StudioXException ex)
        {
            PlannerStatus.Text = ex.Message;
        }
    }

    private void SetupEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (buildingSetup || loading || busy || UartPort?.SelectedItem is not string unit || !Ag32Setup.Catalog.ContainsKey(unit))

        {
            return;
        }
        if (SetupEnabled.IsChecked == true)

        {
            setupDraft.Units.TryAdd(unit, new());
        }
        else

        {
            setupDraft.Units.Remove(unit);
        }
        ShowSetup(unit);
        MarkChanged();
        UpdateDiagram();
    }
}
