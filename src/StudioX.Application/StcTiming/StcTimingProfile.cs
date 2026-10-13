namespace StudioX.Application.StcTiming;

using StudioX.Foundation;
using StudioX.Packages;

public sealed record StcTimingProfile(string Family, string Header, string DeviceId, bool HasTimer2Baud,
    bool HasPinSwitch, IReadOnlyList<int> TimerDividers, string DocumentationUrl)
{
    public string? ClockOutputRegister => Family switch
    {
        "stc12" => "WAKE_CLKO",
        "stc15" => "INT_CLKO",
        "stc8g" or "stc8h" => "INTCLKO",
        _ => null
    };
    public static StcTimingProfile For(DeviceDefinition device)
    {
        if (device.Architecture != "mcs51" || device.ToolsetId != "stc.sdcc")
        {
            throw new StudioXException("STC_TIMING_DEVICE", "51 计时生成需要包内明确声明的 STC SDCC 器件。");
        }
        var stc15 = new[] { "STC15F2K08S2", "STC15F2K16S2", "STC15F2K24S2", "STC15F2K32S2",
            "STC15F2K40S2", "STC15F2K48S2", "STC15F2K56S2", "STC15F2K60S2", "IAP15F2K61S2",
            "STC15L2K08S2", "STC15L2K16S2", "STC15L2K24S2", "STC15L2K32S2", "STC15L2K40S2",
            "STC15L2K48S2", "STC15L2K56S2", "STC15L2K60S2", "IAP15L2K61S2" };
        var family = stc15.Contains(device.Id, StringComparer.Ordinal) ? "stc15" : device.Id switch
        {
            "STC89C52RC" => "stc89",
            "STC12C5A60S2" => "stc12",
            "STC8G1K08" or "STC8G1K17" => "stc8g",
            "STC8H1K08" or "STC8H1K17" => "stc8h",
            _ => throw new StudioXException("STC_TIMING_DEVICE", "该准确型号尚无已核对的 51 计时生成规则。")
        };
        var modern = family is "stc15" or "stc8g" or "stc8h";
        return new(family, family + ".h", device.Id, modern, modern,
            family == "stc89" ? [6, 12] : [1, 12], "https://www.stcmicro.com/datasheet/" + (family switch
            {
                "stc89" => "STC89C51RC-en.pdf",
                "stc12" => "STC12C5A60S2-en.pdf",
                "stc15" => "STC15F2K60S2-en.pdf",
                "stc8g" => "STC8G-cn.pdf",
                _ => "STC8H-en.pdf"
            }));
    }

    public void Validate(StcTimingSettings settings)
    {
        settings.Validate();
        if (!TimerDividers.Contains(settings.TimerDivider) ||
            settings.BaudGenerator == "timer2" && !HasTimer2Baud ||
            settings.UartPins != "default" && !HasPinSwitch)
        {
            throw new StudioXException("STC_TIMING_CAPABILITY", "所选计时分频、波特率发生器或串口引脚不适用于该型号。");
        }
        if (Family == "stc89" && settings.TimerDivider == 6 && settings.ReferenceClockHz > 24000000)
        {
            throw new StudioXException("STC_TIMING_CLOCK", "STC89C52RC 的 6T 配置只允许已核对的 ≤24 MHz 参考时钟。");
        }
    }
}
