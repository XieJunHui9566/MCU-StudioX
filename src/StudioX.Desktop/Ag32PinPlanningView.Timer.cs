namespace StudioX.Desktop;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class Ag32PinPlanningView
{
    private static readonly string[] CaptureFilters = ["无滤波", "原时钟 · 2 次采样", "原时钟 · 4 次采样", "原时钟 · 8 次采样",
        "时钟 / 2 · 6 次采样", "时钟 / 2 · 8 次采样", "时钟 / 4 · 6 次采样", "时钟 / 4 · 8 次采样",
        "时钟 / 8 · 6 次采样", "时钟 / 8 · 8 次采样", "时钟 / 16 · 5 次采样", "时钟 / 16 · 6 次采样",
        "时钟 / 16 · 8 次采样", "时钟 / 32 · 5 次采样", "时钟 / 32 · 6 次采样", "时钟 / 32 · 8 次采样"];

    private sealed class TimerChannelDraft(Ag32TimerChannel? source)
    {
        public string Mode = source?.Duty is not null ? "PWM" : source?.Edge is not null ? "IC" : source?.CompareMode ?? "OFF";
        public bool IsCompare => Mode is "TOGGLE" or "ACTIVE" or "INACTIVE";
        public bool IsOutput => Mode == "PWM" || IsCompare;
        public string CompareTicks = (source?.CompareTicks ?? 0).ToString(CultureInfo.InvariantCulture);
        public string Duty = (source?.Duty ?? 50m).ToString(CultureInfo.InvariantCulture);
        public bool Complementary = source?.Complementary ?? false;
        public string Edge = source?.Edge ?? "RISING";
        public int Divider = source?.Divider ?? 1;
        public int Filter = source?.Filter ?? 0;
        public bool Interrupt = source is { Interrupt: true, Edge: not null };
        public bool CompareInterrupt = source is { Interrupt: true, IsOutput: true };
    }

    private sealed class TimerDraft(Ag32TimerSettings? source)
    {
        public bool Enabled = source is not null;
        public string Frequency = (source?.FrequencyHz ?? 1000m).ToString(CultureInfo.InvariantCulture);
        public string Mode = source?.Encoder ?? "INTERNAL";
        public string Top = (source?.EncoderTop ?? uint.MaxValue).ToString(CultureInfo.InvariantCulture);
        public int Filter = source?.EncoderFilter ?? 0;
        public string DeadTime = (source?.DeadTimeNs ?? 0m).ToString(CultureInfo.InvariantCulture);
        public string Brake = source?.BreakPolarity ?? "OFF";
        public int SelectedChannel;
        public bool UpdateInterrupt = source?.UpdateInterrupt ?? false;
        public bool BreakInterrupt = source?.BreakInterrupt ?? false;
        public TimerChannelDraft[] Channels = (source?.Channels ?? new Ag32TimerChannel?[4]).Select(c => new TimerChannelDraft(c)).ToArray();

        public Ag32TimerSettings Settings(string module, int priority)
        {
            decimal Number(string text, string field) => decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value) ? value : throw new StudioXException("AG32_TIMER_INIT", $"{module} 的{field}需要填写有效数字。");
            if (Mode != "INTERNAL")
            {
                if (!uint.TryParse(Top.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var top) || top == 0)

                {
                    throw new StudioXException("AG32_TIMER_INIT", $"{module} 编码器计数上限应为 1～4294967295。");
                }
                return new(module, 1m, Encoder: Mode, EncoderTop: top, EncoderFilter: Filter)
                {
                    UpdateInterrupt = UpdateInterrupt,
                    InterruptPriority = priority
                };
            }
            var hz = Number(Frequency, "周期频率");
            if (Ag32TimerInitialization.IsBasic(module))
            {
                return new(module, hz)
                {
                    UpdateInterrupt = UpdateInterrupt,
                    InterruptPriority = priority
                };
            }
            Ag32TimerChannel? Channel(int index)
            {
                var c = Channels[index];
                if (c.Mode == "IC")
                {
                    return new(Edge: c.Edge, Divider: c.Divider, Filter: c.Filter, Interrupt: c.Interrupt);
                }
                if (c.IsCompare)
                {
                    if (!uint.TryParse(c.CompareTicks.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))

                    {
                        throw new StudioXException("AG32_TIMER_INIT", $"{module} CH{index} 的 CCR 比较值需要填写非负整数。");
                    }
                    return new(Complementary: c.Complementary, Interrupt: c.CompareInterrupt)
                    {
                        CompareMode = c.Mode,
                        CompareTicks = ticks
                    };
                }
                if (c.Mode != "PWM")
                {
                    return null;
                }
                var duty = Number(c.Duty, $"CH{index} 占空比");
                if (duty is < 0 or > 100)

                {
                    throw new StudioXException("AG32_TIMER_INIT", $"{module} CH{index} 占空比应为 0～100%。");
                }
                return new(duty, c.Complementary, Interrupt: c.CompareInterrupt);
            }
            var output = Channels.Any(c => c.IsOutput);
            return new(module, hz, Channel(0), Channel(1), Channel(2), Channel(3),
                DeadTimeNs: Channels.Any(c => c.IsOutput && c.Complementary) ? Number(DeadTime, "死区 (ns)") : 0m, BreakPolarity: output && Brake != "OFF" ? Brake : null)
            {
                UpdateInterrupt = UpdateInterrupt,
                BreakInterrupt = output && Brake != "OFF" && BreakInterrupt,
                InterruptPriority = priority
            };
        }
    }

    private readonly Dictionary<string, TimerDraft> timerDrafts = [];
    private bool updatingTimer;
    private string? SelectedTimer => UartPort.SelectedItem is string name &&
        (Ag32TimerInitialization.IsGeneral(name) || Ag32TimerInitialization.IsBasic(name)) ? name : null;

    private void LoadTimerDrafts(Ag32PinPlanSnapshot snapshot)
    {
        var previous = timerDrafts.ToDictionary(p => p.Key, p => p.Value.SelectedChannel);
        timerDrafts.Clear();
        foreach (var s in snapshot.Timers)
        {
            timerDrafts.Add(s.Module, new(s)
            {
                SelectedChannel = previous.GetValueOrDefault(s.Module)
            });
        }
    }

    public Ag32TimerSettings[] GetTimers() => timerDrafts.Where(p => p.Value.Enabled).OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => p.Value.Settings(p.Key, VectorPriority(p.Key))).ToArray();

    private void ShowTimerPort()
    {
        if (SelectedTimer is not { } module)
        {
            return;
        }
        if (!timerDrafts.TryGetValue(module, out var d))
        {
            timerDrafts.Add(module, d = new(null));
        }
        var basic = Ag32TimerInitialization.IsBasic(module);
        updatingTimer = true;
        try
        {
            var c = d.Channels[d.SelectedChannel];
            TimerTitle.Text = basic ? $"{module[..6]} · 基本定时器 {module[^1]}" : $"{module} · 通用定时器";
            TimerEnabled.IsChecked = d.Enabled;
            TimerMode.SelectedValue = d.Mode;
            EncoderFilter.ItemsSource = TimerFilter.ItemsSource = CaptureFilters;
            EncoderFilter.SelectedIndex = d.Filter;
            EncoderTop.Text = d.Top;
            TimerFrequency.Text = d.Frequency;
            TimerChannel.ItemsSource = new[] { "CH0", "CH1", "CH2", "CH3" };
            TimerChannel.SelectedIndex = d.SelectedChannel;
            TimerFunction.SelectedValue = c.Mode;
            TimerDuty.Text = c.Duty;
            TimerCompareTicks.Text = c.CompareTicks;
            TimerCompareIrq.IsChecked = c.CompareInterrupt;
            TimerComplement.IsChecked = c.Complementary;
            TimerEdge.SelectedValue = c.Edge;
            TimerFilter.SelectedIndex = c.Filter;
            TimerDivider.SelectedValue = c.Divider.ToString(CultureInfo.InvariantCulture);
            TimerIrq.IsChecked = c.Interrupt;
            TimerDeadTime.Text = d.DeadTime;
            TimerBreak.SelectedValue = d.Brake;
        }
        finally
        {
            updatingTimer = false;
        }
        RefreshTimerPins();
    }

    private void RefreshTimerPins()
    {
        if (SelectedTimer is not { } module || Snapshot is null || !timerDrafts.TryGetValue(module, out var d) ||
            Ag32TimerInitialization.IsBasic(module))
        {
            return;
        }
        updatingTimer = true;
        try
        {
            var reserved = Ag32PinPlanningService.GetReservedAnalogPins(Snapshot.DeviceId, GetAnalog(), setupDraft).Select(p => p.Pin).ToHashSet();
            void Bind(ComboBox picker, string signal)
            {
                picker.Tag = module + "_" + signal;
                BindSignalPin(picker, (string)picker.Tag, reserved);
            }
            Bind(TimerPin, $"CH{d.SelectedChannel}");
            Bind(TimerNPin, $"CHN{d.SelectedChannel}");
            Bind(TimerBreakPin, "BRK");
            Bind(EncoderA, "CH0");
            Bind(EncoderB, "CH1");
        }
        catch (StudioXException)
        {
            TimerPin.ItemsSource = TimerNPin.ItemsSource = TimerBreakPin.ItemsSource = EncoderA.ItemsSource = EncoderB.ItemsSource = null;
        }
        finally
        {
            updatingTimer = false;
        }
    }

    private void TimerPin_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && !updatingTimer && !busy && Snapshot?.CanEdit == true &&
            sender is ComboBox { Tag: string function, SelectedValue: int number })
        {
            AssignPeripheralPin(function, number);
        }
    }

    private void TimerChannel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || updatingTimer || SelectedTimer is not { } module || TimerChannel.SelectedIndex < 0)
        {
            return;
        }
        timerDrafts[module].SelectedChannel = TimerChannel.SelectedIndex;
        ShowTimerPort();
        UpdateTimerPresentation();
    }

    private void TimerField_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || updatingTimer || busy || Snapshot?.CanEdit != true || SelectedTimer is not { } module)
        {
            return;
        }
        var d = timerDrafts[module];
        d.Enabled = TimerEnabled.IsChecked == true;
        d.Mode = (string)TimerMode.SelectedValue;
        d.Frequency = TimerFrequency.Text;
        d.Top = EncoderTop.Text;
        d.Filter = EncoderFilter.SelectedIndex;
        d.DeadTime = TimerDeadTime.Text;
        d.Brake = (string)TimerBreak.SelectedValue;
        var c = d.Channels[d.SelectedChannel];
        c.Mode = (string)TimerFunction.SelectedValue;
        c.Duty = TimerDuty.Text;
        c.CompareTicks = TimerCompareTicks.Text;
        if (ReferenceEquals(sender, TimerCompareIrq))
        {
            c.CompareInterrupt = TimerCompareIrq.IsChecked == true;
        }
        c.Complementary = TimerComplement.IsChecked == true;
        c.Edge = (string)TimerEdge.SelectedValue;
        c.Filter = TimerFilter.SelectedIndex;
        c.Divider = int.Parse((string)TimerDivider.SelectedValue, CultureInfo.InvariantCulture);
        c.Interrupt = TimerIrq.IsChecked == true;
        if (ReferenceEquals(sender, TimerFunction) || ReferenceEquals(sender, TimerComplement))
        {
            ShowSetup(module);
        }
        MarkChanged();
    }

    private string? UpdateTimerPresentation()
    {
        if (Snapshot is null)
        {
            return null;
        }
        var selected = SelectedTimer;
        if (selected is not null && timerDrafts.TryGetValue(selected, out var draft))
        {
            var basic = Ag32TimerInitialization.IsBasic(selected);
            var encoder = draft.Mode != "INTERNAL";
            var c = draft.Channels[draft.SelectedChannel];
            static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
            TimerEnabled.IsEnabled = !busy && Snapshot.CanEdit;
            TimerParameters.IsEnabled = TimerEnabled.IsEnabled && draft.Enabled;
            TimerModeRow.Visibility = Show(!basic);
            TimerFrequencyRow.Visibility = Show(!encoder && !(basic && setupDraft.Value(selected, "Mode") == "自由运行"));
            EncoderRows.Visibility = Show(encoder);
            TimerChannelRows.Visibility = Show(!basic && !encoder);
            TimerPinRow.Visibility = Show(c.Mode != "OFF");
            TimerCaptureRows.Visibility = Show(c.Mode == "IC");
            TimerOutputRows.Visibility = Show(c.IsOutput);
            TimerDutyRow.Visibility = Show(c.Mode == "PWM");
            TimerCompareRow.Visibility = Show(c.IsCompare);
            TimerComplementRow.Visibility = Show(c.Complementary);
            TimerProtection.Visibility = Show(draft.Channels.Any(ch => ch.IsOutput));
            TimerDeadTimeRow.Visibility = Show(draft.Channels.Any(ch => ch.IsOutput && ch.Complementary));
            TimerBreakRow.Visibility = Show(draft.Brake != "OFF");
        }
        string? firstError = null, localError = null;
        var detail = "";
        var code = "";
        foreach (var (module, d) in timerDrafts.Where(p => p.Value.Enabled))
        {
            try
            {
                var generated = Ag32PinPlanningService.PreviewTimer(d.Settings(module, VectorPriority(module)), assignments, GetClocks(), out var timing, setupDraft);
                if (module == selected)
                {
                    (code, detail) = (generated, timing);
                }
            }
            catch (StudioXException ex)
            {
                firstError ??= ex.Message;
                if (module == selected)
                {
                    localError = ex.Message;
                }
            }
        }
        TimerTiming.Text = detail;
        TimerStatus.Text = localError ?? "";
        TimerStatus.Visibility = localError is null ? Visibility.Collapsed : Visibility.Visible;
        TimerCode.Text = code;
        TimerCodePanel.Visibility = code.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        return firstError;
    }
}
