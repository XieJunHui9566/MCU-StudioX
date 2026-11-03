namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

public sealed record StcIspCapabilities(string DeviceId, IReadOnlyList<StcClockMode> SupportedClockModes,
    bool SupportsRcTrim, int MinRcFrequencyHz, int MaxRcFrequencyHz, string Warning)
{
    public bool SupportsNativeUsb => DeviceId == "STC8H8K64U";

    public static StcIspCapabilities For(DeviceDefinition device)
    {
        if (device.Architecture != "mcs51" || device.ToolsetId != "stc.sdcc")
        {
            throw new StudioXException("STC_ISP_DEVICE", "仅 STC SDCC 工程支持串口 ISP。");
        }
        var id = device.Id.ToUpperInvariant();
        if (id.StartsWith("STC12H", StringComparison.Ordinal) || id.StartsWith("STC15H", StringComparison.Ordinal) ||
            id.StartsWith("IAP12H", StringComparison.Ordinal) || id.StartsWith("IAP15H", StringComparison.Ordinal) ||
            id.StartsWith("IRC12H", StringComparison.Ordinal) || id.StartsWith("IRC15H", StringComparison.Ordinal))
        {
            throw new StudioXException("STC_ISP_DEVICE", "STC12H / STC15H 尚未核对独立 ISP 协议，不能套用旧 STC12 / STC15 的下载与时钟能力。");
        }
        var profile = StcHardwareCatalog.Profile(id);
        if (profile == "15a")
        {
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc], true, 5000000, 28000000,
                "小容量 STC15A 协议不提供外部时钟源切换；按该协议进行内部 RC 校准。");
        }
        if (profile == "15")
        {
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc, StcClockMode.ExternalCrystal],
            true, 5000000, 28000000, "外部晶振必须实装并与填写频率一致；切换后若无时钟，程序无法正常运行。stcgal 不会设置晶振物理频率。");
        }
        if (profile is "12" or "12a")
        {
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc, StcClockMode.ExternalCrystal],
            false, 0, 0, "该系列的 stcgal 只支持切换内/外部时钟，不支持 RC 频率校准；外部晶振需已实装。");
        }
        if (profile == "8")
        {
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc],
            true, 4000000, 36000000, "stcgal 1.10 未提供该系列的外部时钟源配置；可选择内部 RC 校准。");
        }
        if (profile == "89")
        {
            return new(device.Id, [StcClockMode.Preserve], false, 0, 0,
            "stcgal 1.10 未提供该系列的 RC 校准或内/外部时钟切换。");
        }
        throw new StudioXException("STC_ISP_DEVICE", "当前 STC 型号尚无经过核对的 ISP 时钟能力。");
    }
}
