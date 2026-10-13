namespace StudioX.Application.StcTiming;

using StudioX.Foundation;

/// <summary>生成参数按工程保存；参考频率不修改 ISP、芯片时钟或串口连接。</summary>
public sealed record StcTimingSettings(int FormatVersion = 1, int? ReferenceClockHz = null,
    int TimerDivider = 12, int DelayMicroseconds = 1000, int DelayTimer = 0,
    int BaudRate = 9600, string BaudGenerator = "timer1", string UartPins = "default")
{
    public const string RelativePath = ".studiox/stc-timing.json";

    public void Validate()
    {
        if (FormatVersion != 1 || ReferenceClockHz is < 1000000 or > 40000000 ||
            TimerDivider is not (1 or 6 or 12) || DelayMicroseconds is < 1 or > 10000000 ||
            DelayTimer is not (0 or 1) || BaudRate is < 300 or > 1000000 ||
            BaudGenerator is not ("timer1" or "timer2") || UartPins is not ("default" or "p36" or "p16"))
        {
            throw new StudioXException("STC_TIMING_SETTINGS", "51 计时参数无效，请检查时钟、计时分频、延时和波特率。");
        }
    }
}
