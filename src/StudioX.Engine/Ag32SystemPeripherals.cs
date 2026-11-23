namespace StudioX.Engine;

using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

public sealed record Ag32SystemPeripherals(int? RtcPrescaler = null, int? WatchdogResetMs = null)
{
    public Ag32Setup Setup { get; init; } = new();
    public int RtcEvents
    {
        get; init;
    }
    public int RtcPriority { get; init; } = 1;
    public bool WatchdogWarning
    {
        get; init;
    }
    public int WatchdogPriority { get; init; } = 1;

    private static readonly Regex Lines = new(@"(?m)^[ \t]*#@StudioX:SYSTEM\b[^\r\n]*(?:\r\n|\r|\n|\z)");
    internal static Ag32SystemPeripherals Read(byte[] bytes)
    {
        var matches = Lines.Matches(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        var setup = Ag32Setup.Read(bytes);
        if (matches.Count == 0)
        {
            return new()
            {
                Setup = setup
            };
        }
        var match = Regex.Match(matches[0].Value.Trim(), @"\A#@StudioX:SYSTEM rtc=(off|\d+) watchdog=(off|\d+)(?: rtcirq=(\d+),(\d+) wdirq=([01]),(\d+))?\z");
        if (matches.Count != 1 || !match.Success)
        {
            throw Error("RTC / 看门狗配置重复或格式有误。");
        }
        static int? Number(string value) => value == "off" ? null : int.TryParse(value, out var n) ? n : -1;
        var settings = new Ag32SystemPeripherals(Number(match.Groups[1].Value), Number(match.Groups[2].Value)) { Setup = setup };
        return !match.Groups[3].Success ? settings : settings with
        {
            RtcEvents = Number(match.Groups[3].Value) ?? 0,
            RtcPriority = Number(match.Groups[4].Value) ?? 1,
            WatchdogWarning = match.Groups[5].Value == "1",
            WatchdogPriority = Number(match.Groups[6].Value) ?? 1
        };
    }

    internal byte[] Write(byte[] bytes)
    {
        var previous = Read(bytes);
        bytes = Setup.Write(bytes);
        if ((previous with
        {
            Setup = Setup
        }) == this)
        {
            return bytes;
        }
        var text = Encoding.UTF8.GetString(bytes);
        var bom = text.StartsWith('\uFEFF');
        text = Lines.Replace(text.TrimStart('\uFEFF'), "");
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        if (RtcPrescaler.HasValue || WatchdogResetMs.HasValue)
        {
            if (text.Length > 0 && text[^1] is not ('\r' or '\n'))
            {
                text += nl;
            }
            var irq = RtcEvents != 0 || WatchdogWarning || RtcPriority != 1 || WatchdogPriority != 1
                ? $" rtcirq={RtcEvents},{RtcPriority} wdirq={(WatchdogWarning ? 1 : 0)},{WatchdogPriority}" : "";
            text += $"#@StudioX:SYSTEM rtc={RtcPrescaler?.ToString() ?? "off"} watchdog={WatchdogResetMs?.ToString() ?? "off"}{irq}{nl}";
        }
        return Encoding.UTF8.GetBytes((bom ? "\uFEFF" : "") + text);
    }

    private static StudioXException Error(string message) => new("AG32_SYSTEM_PERIPHERALS", message);

    public void Validate(uint pclk)
    {
        if (RtcEvents is < 0 or > 7 || RtcPriority is < 1 or > 15 || WatchdogPriority is < 1 or > 15 ||
            RtcEvents != 0 && RtcPrescaler is null || WatchdogWarning && WatchdogResetMs is null)

        {
            throw Error("RTC 或看门狗中断配置无效，请检查外设使能和中断优先级。");
        }
        if (RtcPrescaler is < 0 or > 0xfffff)
        {
            throw Error("RTC 预分频装载值应为 0～1048575。");
        }
        if (WatchdogResetMs is int ms && (ms < 1 || pclk == 0 || ((long)pclk * ms + 1999) / 2000 > uint.MaxValue))

        {
            throw Error($"看门狗复位超时应为 1～{uint.MaxValue * 2000L / Math.Max(pclk, 1u)} ms。");
        }
    }

    internal void Render(Dictionary<string, byte[]> result, uint pclk)
    {
        Validate(pclk);
        var api = new StringBuilder();
        var source = new StringBuilder();
        if (RtcEvents != 0)
        {
            Ag32InterruptCode.Callback(api, source, "void SX_RTC_Interrupt(uint32_t events)",
                "events 包含 RTC_FLAG_SEC、RTC_FLAG_ALR 或 RTC_FLAG_OW，闹钟比较值由应用设置。", "(void)events;");
            source.AppendLine("""
                void RTC_isr(void)
                {
                    const uint16_t events = RTC->CRL & RTC->CRH & (RTC_FLAG_SEC | RTC_FLAG_ALR | RTC_FLAG_OW);
                    if (events) { RTC_ClearFlag(events); SX_RTC_Interrupt(events); }
                }
                """);
        }
        if (RtcPrescaler is int prescaler)
        {
            var clock = Setup.Value("RTC", "Clock");
            bool lse = clock.StartsWith("LSE");
            var output = Setup.Value("RTC", "Output") switch
            {
                "校准时钟" => "CLOCK",
                "秒脉冲" => "SECOND",
                "闹钟" => "ALARM",
                _ => "NONE"
            };
            Ag32PeripheralModule.Rtc.Render(result, api + "/* 所选时钟与预分频配置成功后返回 true。 */\nbool SX_RTC_IsReady(void);",
                source + """
                static bool rtc_ready;
                bool SX_RTC_IsReady(void) { return rtc_ready; }

                /* 使用内核周期计数，在没有外部晶振时也能退出初始化。 */
                static bool rtc_wait(volatile const uint16_t *reg, uint16_t mask)
                {
                    const uint32_t start = read_csr(mcycle);
                    const uint32_t limit = StudioX_SystemClockHz() * 2u;
                    while ((*reg & mask) == 0u)
                        if ((uint32_t)(read_csr(mcycle) - start) >= limit) return false;
                    return true;
                }
                """, $$"""
                    rtc_ready = false;
                    const uint16_t clock = RTC_BDCR_RTCEN | {{(clock == "LSI" ? "RTC_CLK_SOURCE_LSI" : !lse ? "RTC_CLK_SOURCE_LOCAL" : "RTC_BDCR_LSEON | RTC_CLK_SOURCE_LSE" + (clock == "LSE 旁路" ? " | RTC_BDCR_LSEBYP" : ""))}};
                    /* 已选时钟只能通过复位备份域更换，保留现场并报告未就绪。 */
                    uint16_t current = RTC->BDCR & RTC_BDCR_RTCSEL_MASK;
                    if (current && current != (clock & RTC_BDCR_RTCSEL_MASK)) return;
                    if ((RTC->BDCR & (RTC_BDCR_RTCSEL_MASK | RTC_BDCR_LSEBYP | clock)) != clock)
                    {
                        if (!rtc_wait(&RTC->CRL, RTC_FLAG_RTOFF)) return;
                        RTC->BDCR = (RTC->BDCR & ~(RTC_BDCR_RTCSEL_MASK | RTC_BDCR_LSEBYP)) | clock;
                        if (!rtc_wait(&RTC->CRL, RTC_FLAG_RTOFF)) return;
                    }
                    if ({{(!lse ? "" : "!rtc_wait(&RTC->BDCR, RTC_BDCR_LSERDY) || ")}}!rtc_wait(&RTC->CRL, RTC_FLAG_RTOFF)) return;
                    if ((RTC->BDCR & (RTC_BDCR_RTCSEL_MASK | RTC_BDCR_RTCEN)) != (clock & (RTC_BDCR_RTCSEL_MASK | RTC_BDCR_RTCEN))) return;
                    if (RTC_GetPrescaler() != {{prescaler}}u)
                    {
                        RTC->PRLH = {{prescaler >> 16}}u;
                        if (!rtc_wait(&RTC->CRL, RTC_FLAG_RTOFF)) return;
                        RTC->PRLL = {{prescaler & 0xffff}}u;
                        if (!rtc_wait(&RTC->CRL, RTC_FLAG_RTOFF)) return;
                    }
                    {{(Setup.Units.ContainsKey("RTC") ? $"RTC->RTCCR = (RTC->RTCCR & ~(RTC_RTCCR_CAL_MASK | RTC_RTCCR_OUTPUT_MASK)) | {Setup.Number("RTC","Calibration")}u | RTC_OUTPUT_{output};\n    if (!rtc_wait(&RTC->CRL, RTC_FLAG_RTOFF)) return;" : "if (RTC->RTCCR & RTC_RTCCR_OUTPUT_MASK) { RTC->RTCCR &= ~RTC_RTCCR_OUTPUT_MASK; if (!rtc_wait(&RTC->CRL, RTC_FLAG_RTOFF)) return; }")}}
                    rtc_ready = true;
                    {{(RtcEvents == 0 ? "" : $"RTC_ClearFlag({RtcEvents}u);\n    RTC_EnableInt({RtcEvents}u);\n" + Ag32InterruptCode.Enable("RTC", RtcPriority, "    "))}}
                """);
        }
        api.Clear();
        source.Clear();
        if (WatchdogWarning)
        {
            Ag32InterruptCode.Callback(api, source, "void SX_WDOG_Warning(void)",
                "已到达复位超时的一半。由业务代码决定是否喂狗，未喂狗将继续复位倒计时。");
            source.AppendLine("""
                void WATCHDOG0_isr(void)
                {
                    /* 清硬件标志会喂狗。先屏蔽本向量，保留原复位倒计时。 */
                    INT_DisableIRQ(WATCHDOG0_IRQn);
                    SX_WDOG_Warning();
                }
                """);
        }
        if (WatchdogResetMs is int ms)

        {
            Ag32PeripheralModule.Watchdog.Render(result,
                            api + "/* 业务循环完成健康检查后调用，须早于配置的复位超时。 */\nvoid SX_WDOG_Feed(void);",
                            source + "/* 应用确认关键任务有进展后喂狗。 */\nvoid SX_WDOG_Feed(void)\n{\n    WDOG_Feed();\n" +
                                (WatchdogWarning ? $"    INT_EnableIRQ(WATCHDOG0_IRQn, {WatchdogPriority});\n" : "") + "}",
                            $$"""
                    /* 两个计数周期后复位，目标 {{ms}} ms。用 64 位计算，向上取整。 */
                    SYS_EnableAPBClock(APB_MASK_WATCHDOG0);
                    const uint64_t ticks = ((uint64_t)StudioX_PeripheralClockHz() * {{ms}}u + 1999u) / 2000u;
                    if (ticks == 0u || ticks > UINT32_MAX) return;
                    WDOG_Init((uint32_t)ticks);
                    {{(WatchdogWarning ? Ag32InterruptCode.Enable("WATCHDOG0", WatchdogPriority, "    ").TrimStart() : "")}}
                """);
        }
    }
}
