namespace StudioX.Desktop;

using System.ComponentModel;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Engine;

public partial class Ag32PinPlanningView
{
    private const string VerilogModule = "Verilog 顶层端口";
    private sealed record VerilogChoice(Ag32VerilogPort Port)
    {
        public string Label => (Port.IsBus ? $"{Port.Name}[{Port.Left}:{Port.Right}]" : Port.Name)
            + " · " + (Port.Direction switch
            {
                "INPUT" => "输入",
                "OUTPUT" => "输出",
                _ => "双向"
            });
    }

    private sealed class VerilogBit(string signal) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Signal { get; } = signal;
        public string Pin { get; private set; } = "未分配";

        public void SetPin(int number)
        {
            var label = number == 0 ? "未分配" : $"PIN_{number}";
            if (Pin == label)
            {
                return;
            }
            Pin = label;
            PropertyChanged?.Invoke(this, new(nameof(Pin)));
        }
    }

    private bool updatingVerilog;

    private void LoadVerilogPorts(Ag32PinPlanSnapshot snapshot)
    {
        var previous = VerilogBitPicker.SelectedValue as string;
        var ports = snapshot.VerilogPorts.Select(port => new VerilogChoice(port)).ToArray();
        VerilogPortPicker.ItemsSource = ports;
        VerilogInterfaceStatus.Text = snapshot.VerilogSignature is null ? "顶层端口未读取，详见页面上方诊断。" :
            $"user_logic · {ports.Length} 个外部端口";
        if (previous is not null)
        {
            SelectVerilogSignal(previous);
        }
        if (VerilogPortPicker.SelectedIndex < 0 && ports.Length > 0)
        {
            VerilogPortPicker.SelectedIndex = 0;
        }
    }

    private void SelectVerilogSignal(string signal)
    {
        var choice = VerilogPortPicker.Items.OfType<VerilogChoice>().FirstOrDefault(item => item.Port.Signals.Contains(signal));
        if (choice is null)
        {
            return;
        }
        VerilogPortPicker.SelectedItem = choice;
        VerilogBitPicker.SelectedValue = signal;
    }

    private void VerilogPort_Changed(object sender, SelectionChangedEventArgs e)
    {
        var port = (VerilogPortPicker.SelectedItem as VerilogChoice)?.Port;
        updatingVerilog = true;
        try
        {
            VerilogBitPicker.ItemsSource = port?.Signals.Select(signal => new VerilogBit(signal)).ToArray();
            VerilogBitPicker.SelectedIndex = port is null ? -1 : 0;
        }
        finally
        {
            updatingVerilog = false;
        }
        RefreshVerilogPins();
        FocusVerilogBit();
    }

    private void VerilogBit_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updatingVerilog)
        {
            return;
        }
        RefreshVerilogPins();
        FocusVerilogBit();
    }

    private void RefreshVerilogPins()
    {
        if (Snapshot is not { } snapshot || VerilogPinPicker is null)
        {
            return;
        }
        updatingVerilog = true;
        try
        {
            var signal = VerilogBitPicker.SelectedValue as string;
            var current = assignments.FirstOrDefault(pin => pin.Function == signal)?.PinNumber ?? 0;
            var choices = new List<PinChoice> { new(0, "未分配") };
            if (signal is not null)
            {
                var used = assignments.ToLookup(pin => pin.PinNumber);
                var analogSettings = GetAnalog();
                var analog = Ag32PinPlanningService.GetReservedAnalogPins(snapshot.DeviceId, analogSettings, setupDraft)
                    .ToDictionary(pin => pin.Pin, pin => pin.ActiveFunctions(analogSettings, setupDraft));
                foreach (var pin in snapshot.Pins.Where(pin => pin.CanAssign))
                {
                    var occupants = used[pin.Number].Where(item => item.Function != signal).Select(item => item.Function).ToList();
                    if (analog.TryGetValue(pin.Number, out var reserved))
                    {
                        occupants.Add(reserved);
                    }
                    if (UsbPad(pin.Number) is { } usb)
                    {
                        occupants.Add(usb);
                    }
                    var label = $"PIN_{pin.Number}" + (occupants.Count == 0 ? "" : " · 已占用 " + string.Join(" / ", occupants));
                    choices.Add(new(pin.Number, label));
                }
            }
            if (VerilogPinPicker.ItemsSource is not IEnumerable<PinChoice> previous || !previous.SequenceEqual(choices))

            {
                VerilogPinPicker.ItemsSource = choices;
            }
            if (!Equals(VerilogPinPicker.SelectedValue, current))
            {
                VerilogPinPicker.SelectedValue = current;
            }
            VerilogPinPicker.IsEnabled = signal is not null && !busy && snapshot.CanEdit;
            var mapped = 0;
            var bySignal = assignments.ToLookup(pin => pin.Function, StringComparer.Ordinal);
            foreach (var bit in VerilogBitPicker.Items.OfType<VerilogBit>())
            {
                var pin = bySignal[bit.Signal].FirstOrDefault()?.PinNumber ?? 0;
                bit.SetPin(pin);
                if (pin != 0)
                {
                    mapped++;
                }
            }
            var total = VerilogBitPicker.Items.Count;
            VerilogPortUsage.Text = total == 0 ? "" : $"已分配 {mapped} / {total} 位";

        }
        finally
        {
            updatingVerilog = false;
        }
    }

    private void FocusVerilogBit()
    {
        if (loading || !Equals(UartPort.SelectedItem, VerilogModule) || VerilogPinPicker.SelectedValue is not int number)
        {
            return;
        }
        selectedPin = number == 0 ? null : number;
        PackageDiagram.SetSelection(selectedPin);
    }

    private void VerilogPin_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (updatingVerilog || loading || busy || Snapshot is not { CanEdit: true } snapshot ||
            VerilogPinPicker.SelectedValue is not int number || VerilogBitPicker.SelectedValue is not string signal ||
            !snapshot.Functions.Any(function => function.IsVerilog && function.Name == signal))

        {
            return;
        }
        var direction = ((VerilogChoice)VerilogPortPicker.SelectedItem).Port.Direction;
        var old = assignments.Where(pin => pin.Function == signal).ToArray();
        if (old.Length == 1 && old[0].PinNumber == number && old[0].Direction == direction)
        {
            return;
        }
        foreach (var pin in old)
        {
            assignments.Remove(pin);
        }
        if (number != 0)
        {
            assignments.Add(new(signal, number, direction, old.FirstOrDefault()?.Name));
        }
        selectedPin = number == 0 ? null : number;
        MarkChanged();
        UpdateDiagram();
    }
}
