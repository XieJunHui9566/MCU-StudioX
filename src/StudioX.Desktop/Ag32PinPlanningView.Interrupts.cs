namespace StudioX.Desktop;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using StudioX.Engine;

public partial class Ag32PinPlanningView
{
    private sealed record TriggerChoice(string Code, string Label);
    private static readonly TriggerChoice[] GpioTriggers = [new("NONE", "关闭"), new("RISEEDGE", "上升沿"),
        new("FALLEDGE", "下降沿"), new("BOTHEDGE", "双边沿"), new("LOWLEVEL", "低电平"), new("HIGHLEVEL", "高电平")];
    private static readonly int[] Priorities = Enumerable.Range(1, 15).ToArray();
    private readonly Dictionary<string, int> sharedPriorities = [];

    private static string TimerVector(string module) => module.Split('.')[0];
    private int VectorPriority(string module) => sharedPriorities.GetValueOrDefault(TimerVector(module), 1);

    private void LoadVectorPriorities(Ag32PinPlanSnapshot snapshot)
    {
        sharedPriorities.Clear();
        foreach (var pin in snapshot.Assignments.Where(p => p.Interrupt != "NONE"))

        {
            sharedPriorities[pin.Function.Split('_')[0]] = pin.InterruptPriority;
        }
        foreach (var timer in snapshot.Timers.OrderBy(t => t.UpdateInterrupt))

        {
            sharedPriorities[TimerVector(timer.Module)] = timer.InterruptPriority;
        }
    }

    public event EventHandler<string>? CallbackRequested;

    private void WriteCallback_Click(object sender, RoutedEventArgs e)
    {
        if (!busy && sender is Button { DataContext: InterruptHook hook })

        {
            CallbackRequested?.Invoke(this, hook.Name);
        }
    }

    private sealed class InterruptEvent(string label, string hint, Func<bool> enabled, Action<bool> change) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public bool Required
        {
            get; init;
        }
        public bool CanToggle => !Required;
        public string? Callback => System.Text.RegularExpressions.Regex.Match(hint, @"^SX_\w+") is { Success: true } m ? m.Value : null;
        private void NotifyChange()
        {
            Changed?.Invoke();
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new(nameof(Trigger)));
        }
        public string Label => label;
        public string Hint => hint;
        public Func<string>? ReadTrigger
        {
            get; init;
        }
        public Action<string>? SetTrigger
        {
            get; init;
        }
        public Action? Changed
        {
            get; set;
        }
        public Visibility SwitchVisibility => ReadTrigger is null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TriggerVisibility => ReadTrigger is null ? Visibility.Collapsed : Visibility.Visible;
        public TriggerChoice[] Triggers => GpioTriggers;
        public bool Enabled
        {
            get => enabled();
            set
            {
                if (ReadTrigger is null && value != enabled())
                {
                    change(value);
                    NotifyChange();
                }
            }
        }
        public string? Trigger
        {
            get => ReadTrigger?.Invoke();
            set
            {
                if (value is null || SetTrigger is null || value == Trigger)
                {
                    return;
                }
                SetTrigger(value);
                NotifyChange();
            }
        }
    }

    private sealed record InterruptHook(string Name, string Label)
    {
        public string Hint => $"{Name} · 保存配置并打开用户实现";
    }

    private sealed class InterruptVector(string vector, string module, int? pin, Func<int> readPriority, Action<int> writePriority)
        : INotifyPropertyChanged
    {
        public InterruptHook[] Hooks
        {
            get
            {
                var names = Events.Where(e => e.Enabled).Select(e => e.Callback).OfType<string>().Distinct().ToArray();
                return names.Select(n => new InterruptHook(n, names.Length == 1 ? "编写回调" : n.Split('_')[2] switch
                {
                    "DmaComplete" => n.Split('_')[1] + " 完成回调",
                    "DmaError" => n.Split('_')[1] + " 错误回调",
                    "PeriodElapsed" => "周期回调",
                    "Capture" => "捕获回调",
                    "Compare" => "比较回调",
                    "Break" => "刹车回调",
                    _ => "中断回调"
                })).ToArray();
            }
        }
        public Visibility PriorityVisibility => vector.StartsWith("LOCAL_INT") ? Visibility.Collapsed : Visibility.Visible;
        public Visibility FixedPriorityVisibility => vector.StartsWith("LOCAL_INT") ? Visibility.Visible : Visibility.Collapsed;
        public string Vector => vector;
        public string Module => module;
        public int? Pin => pin;
        public string LocationHint => $"定位到{module}的配置";
        public int[] Priorities => Ag32PinPlanningView.Priorities;
        public List<InterruptEvent> Events { get; } = [];
        public bool Enabled => Events.Any(e => e.Enabled);
        public int Priority
        {
            get => readPriority();
            set
            {
                if (value is >= 1 and <= 15 && value != Priority)
                {
                    writePriority(value);
                    PropertyChanged?.Invoke(this, new(nameof(Priority)));
                }
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public void UpdateEnabled()
        {
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
            PropertyChanged?.Invoke(this, new(nameof(Hooks)));
        }
    }

    private void RefreshInterruptRows()
    {
        if (Snapshot is null || !IrqTab.IsSelected)
        {
            return;
        }
        var rows = new List<InterruptVector>();
        InterruptVector Shared(string vector, string module, int? pin = null)
        {
            var row = new InterruptVector(vector, module, pin, () => VectorPriority(vector),
                value =>
                {
                    sharedPriorities[vector] = value;
                    MarkChanged();
                });
            rows.Add(row);
            return row;
        }
        foreach (var group in assignments.Where(p => p.Function.StartsWith("GPIO", StringComparison.Ordinal) && p.Direction == "INPUT")
                     .GroupBy(p => p.Function.Split('_')[0]))
        {
            var row = Shared(group.Key, PinProperties, group.First().PinNumber);
            foreach (var pin in group)
            {
                Ag32PinAssignment Current() => assignments.First(p => p.PinNumber == pin.PinNumber && p.Function == pin.Function);
                var label = $"{pin.Function} · PIN_{pin.PinNumber}" + (pin.Name is null ? "" : $" · {pin.Name}");
                row.Events.Add(new(label, $"SX_{group.Key}_Interrupt(pins)", () => Current().Interrupt != "NONE", _ =>
                {
                })
                {
                    ReadTrigger = () => Current().Interrupt,
                    SetTrigger = value =>
                    {
                        var current = Current();
                        assignments[assignments.IndexOf(current)] = current with
                        {
                            Interrupt = value
                        };
                    }
                });
            }
        }
        foreach (var (port, uart) in uartDrafts.Where(p => p.Value.Enabled))
        {
            var module = uart.Settings.Module;
            void Set(Ag32UartSettings settings) => uartDrafts[port] = uartDrafts[port] with
            {
                Settings = settings
            };
            Ag32UartSettings Read() => uartDrafts[port].Settings;
            var row = new InterruptVector(module, module, null, () => Read().InterruptPriority,
                value =>
                {
                    Set(Read() with
                    {
                        InterruptPriority = value
                    });
                    MarkChanged();
                });
            row.Events.Add(new("接收 / 接收超时", $"在主循环中调用 StudioX_{module}_TryRead() 读取数据。", () => Read().ReceiveInterrupt,
                value => Set(Read() with { ReceiveInterrupt = value })));
            foreach (var item in Ag32UartInitialization.AdditionalEvents)
            {
                row.Events.Add(new(item.Label, $"SX_{module}_Interrupt(events) · {item.Flag}",
                    () => (Read().AdditionalInterruptMask & item.Mask) != 0, value =>
                    {
                        var settings = Read();
                        Set(settings with
                        {
                            AdditionalInterruptMask = value ? settings.AdditionalInterruptMask | item.Mask : settings.AdditionalInterruptMask & ~item.Mask
                        });
                    }));
            }
            rows.Add(row);
        }
        foreach (var name in Ag32Setup.Catalog.Keys.Where(n => n.StartsWith("EXT_INT") || n.StartsWith("LOCAL_INT")))
        {
            var local = name.StartsWith("LOCAL_INT");
            var pin = assignments.FirstOrDefault(a => a.Function == (local ? setupDraft.Value(name, "Source") : name));
            if (!local && pin is null && !setupDraft.Units.ContainsKey(name))
            {
                continue;
            }
            var row = new InterruptVector(name, name, pin?.PinNumber, () => !local && int.TryParse(setupDraft.Value(name, "Priority"), out var p) ? p : 1,
                p =>
                {
                    if (!local && setupDraft.Units.TryGetValue(name, out var values))
                    {
                        values["Priority"] = p.ToString();
                        MarkChanged();
                    }
                });
            row.Events.Add(new(local ? "内核直连" : "外部低电平", $"SX_{name}_Interrupt()", () => setupDraft.Units.ContainsKey(name), enabled =>
            {
                if (enabled)
                {
                    setupDraft.Units.TryAdd(name, new());
                }
                else
                {
                    setupDraft.Units.Remove(name);
                }
                if (Equals(UartPort.SelectedItem, name))
                {
                    ShowSetup(name);
                }
            }));
            rows.Add(row);
        }
        foreach (var unit in setupDraft.Units.Keys.Where(u => Ag32Setup.IrqEvents(u).Length != 0))
        {
            var values = setupDraft.Units[unit];
            var row = new InterruptVector(unit, unit, null, () => setupDraft.Number(unit, "Priority"), p =>
            {
                values["Priority"] = p.ToString();
                MarkChanged();
            });
            foreach (var e in unit == "USB0" ? Ag32Setup.UsbEvents(setupDraft.Value(unit, "Role")) : Ag32Setup.IrqEvents(unit))

            {
                row.Events.Add(new(e.Label, $"SX_{unit}_Interrupt(events)", () => (setupDraft.Number(unit, "Events") & e.Mask) != 0,
                                    enabled => values["Events"] = (enabled ? setupDraft.Number(unit, "Events") | e.Mask : setupDraft.Number(unit, "Events") & ~e.Mask).ToString()));
            }
            rows.Add(row);
        }
        if (RtcEnabled.IsChecked == true)
        {
            var row = new InterruptVector("RTC", "RTC", null, () => rtcPriority, p =>
            {
                rtcPriority = p;
                MarkChanged();
            });
            foreach (var (bit, label) in new[] { (1, "计数周期"), (2, "闹钟比较"), (4, "计数溢出") })

            {
                row.Events.Add(new(label, "SX_RTC_Interrupt(events)", () => (rtcEvents & bit) != 0,
                                    value => rtcEvents = value ? rtcEvents | bit : rtcEvents & ~bit));
            }
            rows.Add(row);
        }
        if (WdogEnabled.IsChecked == true)
        {
            var row = new InterruptVector("WATCHDOG0", "WDOG", null, () => watchdogPriority, p =>
            {
                watchdogPriority = p;
                MarkChanged();
            });
            row.Events.Add(new("复位前预警", "SX_WDOG_Warning()", () => watchdogWarning, value => watchdogWarning = value));
            rows.Add(row);
        }
        foreach (var (vector, priority, hook) in new[] { ("DMAC0_INTTC", "Complete", "Complete"), ("DMAC0_INTERR", "Error", "Error") })
        {
            var channels = setupDraft.Units.Keys.Where(k => k.StartsWith("DMA_CH")).ToArray();
            if (channels.Length == 0)
            {
                break;
            }
            var row = new InterruptVector(vector, "DMA_IRQ", null, () => setupDraft.Number("DMA_IRQ", priority), p =>
            {
                setupDraft.Units.TryAdd("DMA_IRQ", new());
                setupDraft.Units["DMA_IRQ"][priority] = p.ToString();
                MarkChanged();
            });
            foreach (var unit in channels)

            {
                row.Events.Add(new(unit + " 完成与错误通知", $"SX_{unit}_{hook}()", () => setupDraft.Value(unit, "Notify") == "中断",
                                    enabled => setupDraft.Units[unit]["Notify"] = enabled ? "中断" : "轮询"));
            }
            rows.Add(row);
        }
        InterruptVector? dma = null;
        foreach (var module in enabledBuses.Order(StringComparer.Ordinal))
        {
            var row = new InterruptVector(module, module, null, () => busDrafts[module].InterruptPriority, value =>
            {
                busDrafts[module] = busDrafts[module] with
                {
                    InterruptPriority = value
                };
                MarkChanged();
            });
            var busSettings = busDrafts[module];
            if (busSettings.RxDmaChannel.HasValue || busSettings.TxDmaChannel.HasValue)
            {
                if (dma is null)
                {
                    dma = new("DMAC0_INTR", module, null, () => busDrafts[module].DmaPriority, priority =>
                    {
                        foreach (var port in enabledBuses.Where(p => busDrafts[p].RxDmaChannel.HasValue || busDrafts[p].TxDmaChannel.HasValue))

                        {
                            busDrafts[port] = busDrafts[port] with
                            {
                                DmaPriority = priority
                            };
                        }
                        MarkChanged();
                    });
                    rows.Add(dma);
                }
                row.Events.Add(new("DMA 总线传输结束", $"SX_{module}_DmaComplete", () => true, _ =>
                {
                })
                {
                    Required = true
                });
                dma.Events.Add(new($"{module} DMA 完成", $"SX_{module}_DmaComplete", () => true, _ =>
                {
                })
                {
                    Required = true
                });
                dma.Events.Add(new($"{module} DMA 错误", $"SX_{module}_DmaError", () => true, _ =>
                {
                })
                {
                    Required = true
                });
            }
            foreach (var item in Ag32BusInterrupts.Events(module))
            {
                var hint = $"SX_{module}_Interrupt" + (module.StartsWith("SPI", StringComparison.Ordinal) ? " · 启动传输时选择 SPI_INTERRUPT_ON" : "");
                row.Events.Add(new(item.Label, hint, () => (busDrafts[module].InterruptMask & item.Mask) != 0, value =>
                {
                    var bus = busDrafts[module];
                    busDrafts[module] = bus with
                    {
                        InterruptMask = value ? bus.InterruptMask | item.Mask : bus.InterruptMask & ~item.Mask
                    };
                }));
            }
            rows.Add(row);
        }
        foreach (var group in timerDrafts.Where(p => p.Value.Enabled).OrderBy(p => p.Key).GroupBy(p => TimerVector(p.Key)))
        {
            var row = Shared(group.Key, group.First().Key);
            foreach (var (module, draft) in group)
            {
                var basic = Ag32TimerInitialization.IsBasic(module);
                row.Events.Add(new(basic ? $"计数器 {module[^1]} 周期到达" : draft.Mode == "INTERNAL" ? "周期更新" : "计数溢出 / 下溢",
                    $"SX_{group.Key}_PeriodElapsed", () => draft.UpdateInterrupt, value => draft.UpdateInterrupt = value));
                if (basic || draft.Mode != "INTERNAL")
                {
                    continue;
                }
                for (var ch = 0; ch < draft.Channels.Length; ++ch)
                {
                    var c = draft.Channels[ch];
                    if (c.Mode == "IC")

                    {
                        row.Events.Add(new($"CH{ch} 输入捕获", $"SX_{module}_Capture", () => c.Interrupt, value => c.Interrupt = value));
                    }
                    if (c.IsOutput)

                    {
                        row.Events.Add(new($"CH{ch} 比较匹配", $"SX_{module}_Compare", () => c.CompareInterrupt, value => c.CompareInterrupt = value));
                    }
                }
                if (draft.Brake != "OFF" && draft.Channels.Any(c => c.IsOutput))

                {
                    row.Events.Add(new("刹车", $"SX_{module}_Break", () => draft.BreakInterrupt, value => draft.BreakInterrupt = value));
                }
            }
        }
        foreach (var row in rows)

        {
            foreach (var option in row.Events)
            {
                option.Changed = () =>
                            {
                                row.UpdateEnabled();
                                MarkChanged();
                                if (IrqEnabledOnly.IsChecked == true && !row.Enabled)
                                {
                                    RefreshInterruptRows();
                                }
                                else
                                {
                                    UpdateInterruptSummary();
                                }
                            };
            }
        }
        var query = IrqSearch.Text.Trim();
        var visible = rows.Where(r => (IrqEnabledOnly.IsChecked != true || r.Enabled) &&
            (r.Vector.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Events.Any(e =>
                e.Label.Contains(query, StringComparison.OrdinalIgnoreCase)))).OrderBy(r => r.Vector, StringComparer.Ordinal).ToArray();
        InterruptRows.ItemsSource = visible;
        UpdateInterruptSummary();
        IrqEmpty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        IrqEmpty.Text = rows.Count == 0 ? "在“引脚分配”中启用外设初始化，或将 GPIO 配置为输入，即可选择中断。"
            : "没有匹配的中断。可调整搜索条件，或取消“仅显示已启用向量”。";
    }

    private void UpdateInterruptSummary()
    {
        var entries = new List<string>();
        foreach (var row in InterruptRows.Items.OfType<InterruptVector>().Where(r => r.Enabled))
        {
            var file = Ag32Setup.ControllerModule(row.Vector);
            var hooks = row.Events.Where(e => e.Enabled).Select(e => e.Hint).Distinct();
            entries.Add($"{row.Vector}_isr  ·  src/{file}.c  ·  include/{file}.h\n    {string.Join("  /  ", hooks)}");
        }
        IrqCodePanel.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        IrqCodeSummary.Text = string.Join("\n", entries);
    }

    private void IrqFilter_Changed(object sender, RoutedEventArgs e) => RefreshInterruptRows();

    private void OpenInterrupts_Click(object sender, RoutedEventArgs e)
    {
        var module = UartPort.SelectedItem as string;
        IrqSearch.Text = module == PinProperties ? assignments.FirstOrDefault(p => p.PinNumber == selectedPin)?.Function.Split('_')[0] ?? ""
            : module?.StartsWith("DMA_CH") == true ? "DMAC0" : module == "WDOG" ? "WATCHDOG0" : TimerVector(module ?? "");
        IrqEnabledOnly.IsChecked = false;
        IrqTab.IsSelected = true;
    }

    private void LocateIrqModule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InterruptVector row })
        {
            return;
        }
        PinTab.IsSelected = true;
        UartPort.SelectedItem = row.Module;
        RevealNavigation(row.Module);
        if (row.Pin is not int pin)
        {
            return;
        }
        selectedPin = pin;
        AssignmentsGrid.SelectedItem = assignments.FirstOrDefault(p => p.PinNumber == pin);
        PackageDiagram.SetSelection(pin);
        UpdatePinDetails();
    }
}
