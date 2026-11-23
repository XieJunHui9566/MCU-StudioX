namespace StudioX.Desktop;

using System.Windows;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;

public partial class Ag32PinPlanningView
{
    private int rtcEvents, rtcPriority = 1, watchdogPriority = 1;
    private bool watchdogWarning;

    private void LoadSystemDraft(Ag32SystemPeripherals settings)
    {
        setupDraft = new()
        {
            Units = settings.Setup.Units.ToDictionary(p => p.Key, p => new Dictionary<string, string>(p.Value), StringComparer.Ordinal)
        };
        (rtcEvents, rtcPriority, watchdogWarning, watchdogPriority) = (settings.RtcEvents, settings.RtcPriority, settings.WatchdogWarning, settings.WatchdogPriority);
        RtcEnabled.IsChecked = settings.RtcPrescaler.HasValue;
        RtcPrescaler.Text = (settings.RtcPrescaler ?? 32767).ToString();
        WdogEnabled.IsChecked = settings.WatchdogResetMs.HasValue;
        WdogTimeout.Text = (settings.WatchdogResetMs ?? 1000).ToString();
    }

    public Ag32SystemPeripherals GetSystemPeripherals() => new(
        RtcEnabled.IsChecked == true ? int.TryParse(RtcPrescaler.Text, out var prl) ? prl : -1 : null,
        WdogEnabled.IsChecked == true ? int.TryParse(WdogTimeout.Text, out var ms) ? ms : -1 : null)
    {
        Setup = setupDraft,
        RtcEvents = RtcEnabled.IsChecked == true ? rtcEvents : 0,
        RtcPriority = RtcEnabled.IsChecked == true ? rtcPriority : 1,
        WatchdogWarning = WdogEnabled.IsChecked == true && watchdogWarning,
        WatchdogPriority = WdogEnabled.IsChecked == true ? watchdogPriority : 1
    };

    private void SystemField_Changed(object sender, RoutedEventArgs e)
    {
        if (!loading && !busy && Snapshot?.CanEdit == true)
        {
            MarkChanged();
        }
    }

    private string? UpdateSystemPresentation(bool editable)
    {
        RtcEnabled.IsEnabled = WdogEnabled.IsEnabled = editable;
        RtcPrescaler.IsEnabled = editable && RtcEnabled.IsChecked == true;
        WdogTimeout.IsEnabled = editable && WdogEnabled.IsChecked == true;
        SetupInspector.IsEnabled = editable;
        var settings = GetSystemPeripherals();
        RtcDetail.Text = int.TryParse(RtcPrescaler.Text, out var prl) && prl is >= 0 and <= 0xfffff
            ? setupDraft.Value("RTC", "Clock") != "LSE" ? $"每 {prl + 1} 个所选时钟周期计数一次。" : $"计数周期 {(prl + 1) / 32768m:0.######} s。通过 SX_RTC_IsReady() 检查起振结果。" : "";
        if (Snapshot is null)
        {
            return null;
        }
        try
        {
            Ag32PinPlanningService.ValidateSystemPeripherals(settings, GetClocks());
            if (setupDraft.Units.Count != 0)
            {
                setupDraft.Validate(Snapshot.DeviceId, assignments, GetAnalog(), GetUarts(), GetBuses(), GetTimers(), settings, GetClocks());
            }
            return null;
        }
        catch (StudioXException e)
        {
            return e.Message;
        }
    }
}
