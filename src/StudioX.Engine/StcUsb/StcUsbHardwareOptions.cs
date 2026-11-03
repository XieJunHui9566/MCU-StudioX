namespace StudioX.Engine.StcUsb;

/// <summary>F784 / 7.4.12U 的 USB 选项；偏移相对于含 04 命令的 payload，不复用串口 MSR 布局。</summary>
public static class StcUsbHardwareOptions
{
    private sealed record Field(int Offset, byte Mask, IReadOnlyDictionary<string, byte> Values);

    // AiCube 6.97C：44B760 编码、44D2E8 USB 调用、446870 状态解释。
    // 复位位在此目标上为 1=RESET；串口参考类的 0=RESET 不适用于这里。
    private static readonly IReadOnlyDictionary<string, Field> Fields = new Dictionary<string, Field>
    {
        ["reset_pin_enabled"] = Flag(42, 0x10, true),
        ["clock_gain"] = new(41, 0x02, new Dictionary<string, byte> { ["low"] = 0, ["high"] = 0x02 }),
        ["watchdog_por_enabled"] = Flag(43, 0x20, false),
        ["watchdog_stop_idle"] = Flag(43, 0x08, false),
        ["watchdog_prescale"] = new(43, 0x07, Enumerable.Range(0, 8).ToDictionary(
            i => (1 << (i + 1)).ToString(System.Globalization.CultureInfo.InvariantCulture), i => (byte)i)),
        ["low_voltage_reset"] = Flag(42, 0x40, false),
        ["low_voltage_threshold"] = new(42, 0x03, Enumerable.Range(0, 4).ToDictionary(
            i => i.ToString(System.Globalization.CultureInfo.InvariantCulture), i => (byte)(3 - i))),
        ["eeprom_erase_enabled"] = Flag(37, 0x02, true),
        ["bsl_pindetect_enabled"] = Flag(37, 0x01, false),
        ["por_reset_delay"] = new(41, 0x80, new Dictionary<string, byte> { ["short"] = 0, ["long"] = 0x80 })
    };

    public static bool Supports(string deviceId, string fieldId) =>
        deviceId.Equals("STC8H8K64U", StringComparison.OrdinalIgnoreCase) && Fields.ContainsKey(fieldId);

    internal static void Apply(Span<byte> command, StcHardwareOptions? hardware, bool enabled)
    {
        if (!enabled || hardware is null) { return; }
        hardware.Validate("STC8H8K64U", true, StcDownloadMode.NativeUsb);
        foreach (var (id, value) in hardware.Values())
        {
            var field = Fields[id];
            command[field.Offset] = (byte)((command[field.Offset] & ~field.Mask) | field.Values[value]);
        }
    }

    private static Field Flag(int offset, byte mask, bool highMeansEnabled) => new(offset, mask,
        new Dictionary<string, byte>
        {
            ["true"] = highMeansEnabled ? mask : (byte)0,
            ["false"] = highMeansEnabled ? (byte)0 : mask
        });
}
