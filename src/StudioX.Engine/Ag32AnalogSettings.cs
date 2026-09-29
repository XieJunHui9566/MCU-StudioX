namespace StudioX.Engine;

/// <summary>模拟 IP 使能与固定引脚预留；通道位号使用 ADC_IN0…15，内部温度无需占脚。</summary>
public sealed record Ag32AnalogSettings(bool Enabled = false, uint AdcChannels = 0,
    bool Dac0 = false, bool Dac1 = false, bool Comparator = false);

public sealed record Ag32AnalogPin(int Pin, string Functions);
