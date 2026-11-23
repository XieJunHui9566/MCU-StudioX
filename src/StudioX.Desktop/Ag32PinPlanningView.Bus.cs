namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class Ag32PinPlanningView
{
    private sealed record SamplePoint(int Value)
    {
        public string Label => $"{Value / 10m:0.#}%";
    }

    private sealed record BusChoice(string Code, string Label);
    private static readonly BusChoice[] DmaChannels = [new("off", "关闭"), .. Enumerable.Range(0, 8).Select(i => new BusChoice(i.ToString(), $"通道 {i}"))];
    private static readonly BusChoice[] Filters = [new("ALL", "接收全部"), new("STD", "标准 ID 精确匹配"),
        new("STD_MASK", "标准 ID 按掩码匹配"), new("EXT", "扩展 ID 精确匹配"), new("EXT_MASK", "扩展 ID 按掩码匹配")];
    private string idDraft = "0", maskDraft = "7FF";

    private readonly Dictionary<string, Ag32BusSettings> busDrafts = [];
    private readonly HashSet<string> enabledBuses = [];
    private readonly List<ComboBox> busPins = [];
    private bool updatingBus;
    private string? SelectedBus => UartPort.SelectedItem is string module && Ag32BusInitialization.Supports(module) ? module : null;

    private void LoadBusDrafts(Ag32PinPlanSnapshot snapshot)
    {
        busDrafts.Clear();
        enabledBuses.Clear();
        var can = snapshot.Buses.FirstOrDefault(b => b.Module == "CAN0");
        idDraft = (can?.FilterId ?? 0).ToString("X");
        maskDraft = can?.FilterMode.EndsWith("_MASK", StringComparison.Ordinal) == true ? can.CompareMask.ToString("X") : "7FF";
        foreach (var settings in snapshot.Buses)
        {
            busDrafts.Add(settings.Module, settings);
            enabledBuses.Add(settings.Module);
        }
    }

    public Ag32BusSettings[] GetBuses() => enabledBuses.Order(StringComparer.Ordinal).Select(module => busDrafts[module]).ToArray();

    private void ShowBusPort()
    {
        if (Snapshot is null || SelectedBus is not { } module)
        {
            return;
        }
        var spi = module.StartsWith("SPI", StringComparison.Ordinal);
        var can = module == "CAN0";
        var settings = busDrafts.GetValueOrDefault(module) ?? (spi ? new(module, ClockDivider: 8)
            : can ? new(module, BitRate: 500000, SamplePointPermille: 875) : new(module, BitRate: 100000));
        updatingBus = true;
        try
        {
            BusTitle.Text = module + (can ? " 经典 CAN" : " 主机");
            BusEnabled.IsChecked = enabledBuses.Contains(module);
            int[] rates = spi ? [2, 4, 8, 16, 32, 64, 128, 256] : can
                ? [10000, 20000, 50000, 100000, 125000, 250000, 500000, 800000, 1000000] : [100000, 400000, 1000000];
            var rate = (settings.ClockDivider ?? settings.BitRate)!.Value;
            BusRate.ItemsSource = rates.Append(rate).Distinct().Order().ToArray();
            BusRate.SelectedItem = rate;
            BusRateLabel.Content = spi ? "分频" : can ? "位率" : "SCL (Hz)";
            BusRate.ToolTip = spi ? "SPI 串行时钟分频系数" : can ? "标称位率（bit/s）" : "I²C 时钟频率（Hz）";
            CanSampleRow.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
            if (settings.SamplePointPermille is int sample)
            {
                CanSample.ItemsSource = new[] { 750, 800, 875, sample }.Distinct().Order().Select(n => new SamplePoint(n)).ToArray();
                CanSample.SelectedValue = sample;
            }
            SpiBigEndian.Visibility = spi ? Visibility.Visible : Visibility.Collapsed;
            SpiBigEndian.IsChecked = settings.BigEndian;
            CanFilterRows.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
            SpiDmaRows.Visibility = spi ? Visibility.Visible : Visibility.Collapsed;
            if (can)
            {
                CanFilterMode.ItemsSource = Filters;
                CanFilterMode.SelectedValue = settings.FilterMode;
                CanFilterId.Text = idDraft;
                CanFilterMask.Text = maskDraft;
                ShowFilterFields(settings.FilterMode);
            }
            if (spi)
            {
                SpiRxDma.ItemsSource = SpiTxDma.ItemsSource = DmaChannels;
                SpiRxDma.SelectedValue = settings.RxDmaChannel?.ToString() ?? "off";
                SpiTxDma.SelectedValue = settings.TxDmaChannel?.ToString() ?? "off";
            }
            BusCodePanel.IsExpanded = false;
            BusPinRows.Children.Clear();
            busPins.Clear();
            (string Signal, string Label)[] signals = spi
                ? [("SCK", "时钟 SCK"), ("SI_IO0", "数据 IO0"), ("SO_IO1", "数据 IO1"), ("CSN", "硬件片选"), ("WPN_IO2", "数据 IO2"), ("HOLDN_IO3", "数据 IO3")]
                : can ? [("RX0", "接收 RX"), ("TX0", "发送 TX")] : [("SCL", "时钟 SCL"), ("SDA", "数据 SDA")];
            foreach (var (signal, label) in signals)
            {
                var function = module + "_" + signal;
                if (!Snapshot.Functions.Any(f => f.Name == function))
                {
                    continue;
                }
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
                var picker = new ComboBox { Tag = function, SelectedValuePath = "Number", DisplayMemberPath = "Label" };
                picker.SelectionChanged += BusPin_Changed;
                var caption = new Label { Content = label, Target = picker, Width = 92, Padding = new Thickness(2, 4, 2, 4) };
                caption.SetResourceReference(Control.ForegroundProperty, "Text");
                row.Children.Add(caption);
                row.Children.Add(picker);
                BusPinRows.Children.Add(row);
                busPins.Add(picker);
            }
        }
        finally
        {
            updatingBus = false;
        }
        RefreshBusPins();
    }

    private void BindSignalPin(ComboBox picker, string function, IReadOnlySet<int> reserved)
    {
        if (Snapshot is not { } snapshot)
        {
            return;
        }
        var current = assignments.FirstOrDefault(item => item.Function == function)?.PinNumber ?? 0;
        var choices = new List<PinChoice> { new(0, "未分配") };
        if (snapshot.Functions.Any(item => item.Name == function))

        {
            choices.AddRange(snapshot.Pins.Where(pin => pin.Number == current || pin.CanAssign && UsbPad(pin.Number) is null && !reserved.Contains(pin.Number) &&
                            !assignments.Any(item => item.PinNumber == pin.Number)).OrderBy(pin => pin.Number)
                            .Select(pin => new PinChoice(pin.Number, $"PIN_{pin.Number}")));
        }
        picker.ItemsSource = choices;
        picker.SelectedValue = current;
    }

    private void RefreshBusPins()
    {
        if (SelectedBus is null || Snapshot is null)
        {
            return;
        }
        updatingBus = true;
        try
        {
            var reserved = Ag32PinPlanningService.GetReservedAnalogPins(Snapshot.DeviceId, GetAnalog(), setupDraft).Select(p => p.Pin).ToHashSet();
            foreach (var picker in busPins)
            {
                BindSignalPin(picker, (string)picker.Tag, reserved);
            }
        }
        catch (StudioXException)
        {
            foreach (var picker in busPins)
            {
                picker.ItemsSource = null;
            }
        }
        finally
        {
            updatingBus = false;
        }
    }

    private void BusPin_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && !updatingBus && !busy && Snapshot?.CanEdit == true &&
            sender is ComboBox { Tag: string function, SelectedValue: int number })
        {
            AssignPeripheralPin(function, number);
        }
    }

    private void ShowFilterFields(string mode)
    {
        CanIdRow.Visibility = mode != "ALL" ? Visibility.Visible : Visibility.Collapsed;
        CanMaskRow.Visibility = mode.EndsWith("_MASK", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static int HexValue(string text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        return int.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : -1;
    }

    private void BusField_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || updatingBus || busy || Snapshot?.CanEdit != true || SelectedBus is not { } module || BusRate.SelectedItem is not int value)
        {
            return;
        }
        var previous = busDrafts.GetValueOrDefault(module);
        Ag32BusSettings settings = module.StartsWith("SPI", StringComparison.Ordinal) ? new(module, value, SpiBigEndian.IsChecked == true)
            : new(module, BitRate: value, SamplePointPermille: module == "CAN0" ? CanSample.SelectedValue as int? : null);
        if (module == "CAN0")
        {
            var mode = CanFilterMode.SelectedValue as string ?? "ALL";
            idDraft = CanFilterId.Text;
            maskDraft = CanFilterMask.Text;
            ShowFilterFields(mode);
            settings = settings with
            {
                FilterMode = mode,
                FilterId = mode == "ALL" ? 0 : HexValue(idDraft),
                CompareMask = mode.EndsWith("_MASK", StringComparison.Ordinal) ? HexValue(maskDraft) : 0
            };
        }
        if (settings.ClockDivider.HasValue)

        {
            settings = settings with
            {
                RxDmaChannel = int.TryParse(SpiRxDma.SelectedValue as string, out var rx) ? rx : null,
                TxDmaChannel = int.TryParse(SpiTxDma.SelectedValue as string, out var tx) ? tx : null
            };
        }
        if (settings.RxDmaChannel.HasValue || settings.TxDmaChannel.HasValue)

        {
            settings = settings with
            {
                DmaPriority = GetBuses().FirstOrDefault(b => b.RxDmaChannel.HasValue || b.TxDmaChannel.HasValue)?.DmaPriority ?? 1
            };
        }
        busDrafts[module] = settings with
        {
            InterruptMask = previous?.InterruptMask ?? 0,
            InterruptPriority = previous?.InterruptPriority ?? 1
        };
        if (BusEnabled.IsChecked == true)
        {
            enabledBuses.Add(module);
        }
        else
        {
            enabledBuses.Remove(module);
        }
        MarkChanged();
    }

    private string? UpdateBusPresentation()
    {
        if (Snapshot is null)
        {
            return null;
        }
        var selected = SelectedBus;
        var editable = !busy && Snapshot.CanEdit;
        BusPinRows.IsEnabled = BusEnabled.IsEnabled = editable;
        BusParameters.IsEnabled = editable && BusEnabled.IsChecked == true;
        var detail = "";
        var code = "";
        string? firstError = null, localError = null;
        foreach (var item in GetBuses())
        {
            try
            {
                var generated = Ag32PinPlanningService.PreviewBus(item, assignments, GetClocks(), out var timing, setupDraft);
                if (item.Module == selected)
                {
                    (code, detail) = (generated, timing);
                }
            }
            catch (StudioXException ex)
            {
                firstError ??= ex.Message;
                if (item.Module == selected)
                {
                    localError = ex.Message;
                }
            }
        }
        if (Ag32BusInitialization.DmaConflict(GetBuses()) is { } conflict)
        {
            firstError ??= conflict;
            if (selected?.StartsWith("SPI", StringComparison.Ordinal) == true)
            {
                localError ??= conflict;
            }
        }
        BusTiming.Text = detail;
        BusStatus.Text = localError ?? "";
        BusStatus.Visibility = localError is null ? Visibility.Collapsed : Visibility.Visible;
        BusCode.Text = code;
        BusCodePanel.Visibility = code.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        return firstError;
    }
}
