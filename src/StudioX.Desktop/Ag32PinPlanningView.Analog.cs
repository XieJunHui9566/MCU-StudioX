namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Controls;
using StudioX.Application;
using StudioX.Engine;

public partial class Ag32PinPlanningView
{
    private const string AnalogModule = "ADC / DAC / 比较器";
    private Ag32AnalogPin[] analogPinout = [];

    private void LoadAnalog(Ag32PinPlanSnapshot snapshot)
    {
        var draft = snapshot.Analog ?? new();
        analogPinout = Ag32PinPlanningService.GetAnalogPins(snapshot.DeviceId);
        AnalogEnabled.IsChecked = draft.Enabled;
        AnalogChannels.Children.Clear();
        for (var channel = 0; channel < 16; channel++)
        {
            var pin = analogPinout.SingleOrDefault(p => p.AdcChannel == channel);
            var selected = (draft.AdcChannels & (1u << channel)) != 0;
            // 旧工程可能保存了未引出的通道；保留原选项让用户明确取消后再生成。
            if (pin is null && !selected)
            {
                continue;
            }
            var label = $"ADC_IN{channel} · " + (pin is null ? "未引出" : $"PIN_{pin.Pin}");
            var choice = new CheckBox
            {
                Tag = channel,
                Content = new TextBlock { Text = label },
                IsChecked = selected,
                Margin = new Thickness(0, 2, 12, 2)
            };
            System.Windows.Automation.AutomationProperties.SetName(choice, label);
            choice.Checked += Analog_Changed;
            choice.Unchecked += Analog_Changed;
            AnalogChannels.Children.Add(choice);
        }
        SetDac(AnalogDac0, "DAC0", draft.Dac0);
        SetDac(AnalogDac1, "DAC1", draft.Dac1);
        AnalogComparator.IsChecked = draft.Comparator;
        AnalogComparatorPins.Text = "可选输入脚：" + string.Join("、", analogPinout.Where(p => p.ComparatorInput is not null)
            .Select(p => $"{p.ComparatorInput} (PIN_{p.Pin})"));

        void SetDac(CheckBox box, string dac, bool selected)
        {
            var pin = analogPinout.SingleOrDefault(p => p.Supports(dac));
            box.Tag = pin?.Pin;
            box.IsChecked = selected;
            box.Content = new TextBlock { Text = dac + (pin is null ? " · 未引出" : $" · PIN_{pin.Pin}") };
        }
    }

    private void SetAnalogEditable(bool editable)
    {
        AnalogEnabled.IsEnabled = editable;
        var enabled = editable && AnalogEnabled.IsChecked == true;
        foreach (CheckBox choice in AnalogChannels.Children)
        {
            var pin = analogPinout.FirstOrDefault(p => p.AdcChannel == (int)choice.Tag);
            choice.IsEnabled = enabled && (choice.IsChecked == true || pin is not null);
            choice.FontWeight = pin is not null && selectedPin == pin.Pin ? FontWeights.Bold : FontWeights.Normal;
        }
        AnalogDac0.IsEnabled = enabled && (AnalogDac0.Tag is int || AnalogDac0.IsChecked == true);
        AnalogDac1.IsEnabled = enabled && (AnalogDac1.Tag is int || AnalogDac1.IsChecked == true);
        AnalogComparator.IsEnabled = enabled && (AnalogComparator.IsChecked == true || analogPinout.Any(p => p.ComparatorInput is not null));
    }

    public Ag32AnalogSettings GetAnalog()
    {
        if (AnalogEnabled.IsChecked != true)
        {
            return new();
        }
        var channels = AnalogChannels.Children.OfType<CheckBox>().Where(c => c.IsChecked == true)
            .Aggregate(0u, (mask, choice) => mask | (1u << (int)choice.Tag));
        var settings = new Ag32AnalogSettings(true, channels, AnalogDac0.IsChecked == true, AnalogDac1.IsChecked == true, AnalogComparator.IsChecked == true);
        if (Snapshot is { } snapshot)
        {
            Ag32PinPlanningService.ValidateAnalogSettings(snapshot.DeviceId, settings);
        }
        return settings;
    }

    private void OpenAnalogPin(int number)
    {
        UartPort.SelectedItem = AnalogModule;
        var channel = analogPinout.FirstOrDefault(p => p.Pin == number)?.AdcChannel;
        var choice = AnalogChannels.Children.OfType<CheckBox>().FirstOrDefault(c => (int)c.Tag == channel);
        choice?.BringIntoView();
    }

    private void Analog_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || Snapshot is null)
        {
            return;
        }
        if (sender is CheckBox { Tag: int tag, IsChecked: true } choice)
        {
            var pin = AnalogChannels.Children.Contains(choice) ? analogPinout.FirstOrDefault(p => p.AdcChannel == tag)?.Pin : tag;
            if (pin is int number)
            {
                selectedPin = number;
                AssignmentsGrid.SelectedItem = assignments.FirstOrDefault(item => item.PinNumber == number);
            }
        }
        MarkChanged();
        UpdateDiagram();
    }
}
