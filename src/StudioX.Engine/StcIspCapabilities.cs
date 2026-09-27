namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

public sealed record StcIspCapabilities(string DeviceId, IReadOnlyList<StcClockMode> SupportedClockModes,
    bool SupportsRcTrim, int MinRcFrequencyHz, int MaxRcFrequencyHz, string Warning)
{
    public static StcIspCapabilities For(DeviceDefinition device)
    {
        if (device.Architecture != "mcs51" || device.ToolsetId != "stc.sdcc")
        {
            throw new StudioXException("STC_ISP_DEVICE", "仅 STC SDCC 工程支持串口 ISP。");
        }
        var id = device.Id.ToUpperInvariant();
        if (id.StartsWith("STC15", StringComparison.Ordinal) || id.StartsWith("IAP15", StringComparison.Ordinal))
        {
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc, StcClockMode.ExternalCrystal],
            true, 5000000, 28000000, "外部晶振必须实装并与填写频率一致；切换后若无时钟，程序无法正常运行。stcgal 不会设置晶振物理频率。");
        }
        if (id.StartsWith("STC12", StringComparison.Ordinal))
        {
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc, StcClockMode.ExternalCrystal],
            false, 0, 0, "该系列的 stcgal 只支持切换内/外部时钟，不支持 RC 频率校准；外部晶振需已实装。");
        }
        if (id.StartsWith("STC8G", StringComparison.Ordinal) || id.StartsWith("STC8H", StringComparison.Ordinal))
        {
            return new(device.Id, [StcClockMode.Preserve, StcClockMode.InternalRc],
            true, 4000000, 36000000, "stcgal 1.10 未提供该系列的外部时钟源配置；可选择内部 RC 校准。");
        }
        if (id.StartsWith("STC89", StringComparison.Ordinal))
        {
            return new(device.Id, [StcClockMode.Preserve], false, 0, 0,
            "stcgal 1.10 未提供该系列的 RC 校准或内/外部时钟切换。");
        }
        throw new StudioXException("STC_ISP_DEVICE", "当前 STC 型号尚无经过核对的 ISP 时钟能力。");
    }
}
