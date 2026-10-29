namespace StudioX.Packages;

/// <summary>固定 Zephyr 版本中的 SoC 与器件容量交集；不包含用户板上的外部连线。</summary>
public sealed record ZephyrDeviceProfile(string DeviceId, string Architecture,
    uint FlashOrigin, uint FlashBytes, uint RamOrigin, uint RamBytes,
    string Soc, string SocConfig, string Dtsi, int InternalClockHz,
    IReadOnlyDictionary<string, string> Evidence);
