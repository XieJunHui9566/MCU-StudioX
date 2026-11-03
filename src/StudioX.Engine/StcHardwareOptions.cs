namespace StudioX.Engine;

using System.Text.Json;
using System.Text.Json.Serialization;
using StudioX.Engine.StcUsb;
using StudioX.Foundation;

/// <summary>空值表示保留本次握手读到的配置；不将另一颗芯片的默认 MSR 当作本机值。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StcHardwareOptions
{
    [JsonPropertyName("reset_pin_enabled")] public string? ResetPin { get; init; }
    [JsonPropertyName("clock_gain")] public string? ClockGain { get; init; }
    [JsonPropertyName("watchdog_por_enabled")] public string? WatchdogPowerOn { get; init; }
    [JsonPropertyName("watchdog_stop_idle")] public string? WatchdogStopIdle { get; init; }
    [JsonPropertyName("watchdog_prescale")] public string? WatchdogPrescale { get; init; }
    [JsonPropertyName("low_voltage_reset")] public string? LowVoltageReset { get; init; }
    [JsonPropertyName("low_voltage_threshold")] public string? LowVoltageThreshold { get; init; }
    [JsonPropertyName("eeprom_lvd_inhibit")] public string? EepromLowVoltageInhibit { get; init; }
    [JsonPropertyName("eeprom_erase_enabled")] public string? EepromErase { get; init; }
    [JsonPropertyName("bsl_pindetect_enabled")] public string? DownloadPinDetect { get; init; }
    [JsonPropertyName("por_reset_delay")] public string? PowerOnDelay { get; init; }
    [JsonPropertyName("rstout_por_state")] public string? ResetOutputLevel { get; init; }
    [JsonPropertyName("uart2_passthrough")] public string? Uart2Passthrough { get; init; }
    [JsonPropertyName("uart2_pin_mode")] public string? Uart2PinMode { get; init; }
    [JsonPropertyName("cpu_core_voltage")] public string? CoreVoltage { get; init; }
    [JsonPropertyName("uart1_remap")] public string? Uart1Remap { get; init; }
    [JsonPropertyName("epwm_open_drain")] public string? EpwmOpenDrain { get; init; }
    [JsonPropertyName("cpu_6t_enabled")] public string? Cpu6T { get; init; }
    [JsonPropertyName("ale_enabled")] public string? Ale { get; init; }
    [JsonPropertyName("xram_enabled")] public string? Xram { get; init; }
    [JsonPropertyName("oscillator_stable_delay")] public string? OscillatorDelay { get; init; }

    public IReadOnlyDictionary<string, string> Values() => JsonSerializer.SerializeToElement(this).EnumerateObject()
        .Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!);

    public static StcHardwareOptions FromValues(IReadOnlyDictionary<string, string> values) =>
        JsonSerializer.Deserialize<StcHardwareOptions>(JsonSerializer.Serialize(values), new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        }) ?? new();

    public void Validate(string deviceId, bool enabled, StcDownloadMode mode)
    {
        var fields = StcHardwareCatalog.For(deviceId);
        foreach (var (name, value) in Values())
        {
            var field = fields.SingleOrDefault(f => f.Id == name);
            if (field is null || !field.Values.Any(v => v.Value == value))
            {
                throw new StudioXException("STC_HARDWARE_OPTION", $"{deviceId} 不支持硬件选项 {name}={value}；未访问硬件。");
            }
        }
        if (enabled && mode == StcDownloadMode.NativeUsb)
        {
            var unsupported = Values().Keys.Where(id => !StcUsbHardwareOptions.Supports(deviceId, id)).ToArray();
            if (unsupported.Length > 0)
            {
                var labels = unsupported.Select(id => fields.Single(f => f.Id == id).Label);
                throw new StudioXException("STC_USB_OPTIONS", $"原生 USB 尚未支持这些硬件选项：{string.Join("、", labels)}。请将其设为保留，或切换串口 ISP；草稿未清除，未访问硬件。");
            }
        }
    }
}
