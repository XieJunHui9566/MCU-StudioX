namespace StudioX.Engine;

/// <summary>VE 中显式设置的 MHz 值；空值保持未声明，BUSCLK 为零沿用系统时钟。</summary>
public sealed record Ag32PinClockSettings(decimal? HseMhz, decimal? SysMhz, decimal? BusMhz);
