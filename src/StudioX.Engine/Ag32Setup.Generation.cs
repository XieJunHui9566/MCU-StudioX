namespace StudioX.Engine;

using System.Text;

public sealed partial class Ag32Setup
{
    private const string BackupWait = """
        static bool backup_wait(void)
        {
            const uint32_t start = read_csr(mcycle);
            while (!(RTC->CRL & RTC_FLAG_RTOFF))
                if ((uint32_t)(read_csr(mcycle) - start) >= StudioX_SystemClockHz()) return false;
            return true;
        }
        """;

    internal void Generate(Dictionary<string, byte[]> files, Ag32TimerSettings[] timers, bool spiDma)
    {
        var modules = new Dictionary<string, (StringBuilder Api, StringBuilder Src, StringBuilder Init)>();
        foreach (var unit in Units.Keys.Order(StringComparer.Ordinal))
        {
            var name = unit.StartsWith("ADC") || unit.StartsWith("DAC") || unit.StartsWith("CMP") ? "ANALOG" :
                unit.StartsWith("EXT_INT") || unit.StartsWith("LOCAL_INT") ? "EXTIRQ" : unit.StartsWith("DMA_CH") ? "DMA" :
                unit switch
                {
                    "HyperRAM" => "HYPERRAM",
                    "FLASH" => "FLASHIRQ",
                    "FCB0" => "FCBIRQ",
                    "CRC0" => "CRC",
                    "BKP" => "BKP",
                    "PWR" => "POWER",
                    "USB0" => "USB",
                    "MAC0" => "ETH",
                    "IWDG" => "IWDG",
                    _ => ""
                };
            if (name == "")

            {
                continue;
            }
            if (!modules.TryGetValue(name, out var m))

            {
                modules[name] = m = (new(), new(), new());
            }
            var (api, src, init) = m;
            string V(string key) => Value(unit, key);
            if (name == "HYPERRAM")
            {
                api.AppendLine("/* 映射容量为 8 MiB，按 32 位字访问。 */")
                    .AppendLine("#define SX_HYPERRAM_WORDS (0x00800000u / sizeof(uint32_t))")
                    .AppendLine("#define SX_HYPERRAM_DATA ((volatile uint32_t *)0x60000000u)");
                var cr0 = V("Latency") == "固定延迟" ? "8F2F" : "8F27";
                init.AppendLine($"    *(volatile uint16_t *)0x60801000u = 0x{cr0}u;");
            }
            if (name == "CRC")

            {
                init.AppendLine($$"""
                        SYS_EnableAHBClock(AHB_MASK_CRC0);
                        CRC_SetPolySize(CRC0, CRC_CR_POLY{{V("Width")}});
                        CRC_SetPolyCoef(CRC0, 0x{{CrcValue("Polynomial"):X}}u);
                        CRC_SetInitData(CRC0, 0x{{CrcValue("Seed"):X}}u);
                        CRC_SetReverseInput(CRC0, CRC_CR_REV_IN_{{V("Reverse")}});
                        CRC_SetOutputDataReverseMode(CRC0, CRC_CR_REV_OUT_{{(V("ReverseOut") == "开启" ? "BIT" : "NONE")}});
                        CRC_SetEndian(CRC0, CRC_CR_{{V("Endian")}}_ENDIAN);
                        CRC_ResetData(CRC0);
                    """);
            }
            if (name == "BKP")
            {
                api.AppendLine("/* 按硬件寄存器编号访问，每个寄存器 16 位。越界或写入超时返回 false。 */")
                    .AppendLine("bool SX_BKP_Read(unsigned index, uint16_t *value);\nbool SX_BKP_Write(unsigned index, uint16_t value);");
                src.AppendLine(BackupWait).AppendLine($$"""
                    bool SX_BKP_Read(unsigned index, uint16_t *value)
                    {
                        if (!value || index - {{Number(unit,"First")}}u > {{Number(unit,"Last") - Number(unit,"First")}}u) return false;
                        *value = RTC_ReadBackupRegister(index);
                        return true;
                    }
                    bool SX_BKP_Write(unsigned index, uint16_t value)
                    {
                        if (index - {{Number(unit,"First")}}u > {{Number(unit,"Last") - Number(unit,"First")}}u || !backup_wait()) return false;
                        RTC->BKP_DR[index].REG = value;
                        return backup_wait();
                    }
                    """);
                init.AppendLine("    /* 备份域由电池维持，启动时保留 DR0～DR15。 */");
            }
            if (name == "ANALOG")
            {
                if (unit.StartsWith("ADC"))
                {
                    var mode = V("Mode");
                    api.AppendLine($"/* 按配置的顺序启动转换。 */\nvoid SX_{unit}_Start(void);");
                    var start = mode switch
                    {
                        "连续转换" => "ADC_StartContinuous",
                        "DMA 连续" => "ADC_StartDma",
                        _ => "ADC_Start"
                    };
                    src.AppendLine($"void SX_{unit}_Start(void) {{ {start}({unit}, {Number(unit, "Divider")}u); }}");
                    var sequence = Sequence(unit);
                    init.AppendLine($"    ADC_SetSeqLength({unit}, ADC_SEQ_LENGTH{sequence.Length});");
                    for (int i = 0; i < sequence.Length; i++)

                    {
                        init.AppendLine($"    ADC_SetSeqChannel({unit}, {i}u, ADC_CHANNEL{sequence[i]});");
                    }
                    if (mode is "上电单次" or "连续转换")

                    {
                        init.AppendLine($"    SX_{unit}_Start();");
                    }
                }
                else if (unit.StartsWith("DAC"))
                {
                    api.AppendLine($"void SX_{unit}_Start(void);");
                    var enable = V("Buffer") == "启用" ? "DAC_Enable" : "DAC_EnableNoBuf";
                    src.AppendLine($"void SX_{unit}_Start(void) {{ {enable}({unit}); }}");
                    init.AppendLine($"    DAC_SetData({unit}, {Number(unit, "Value")}u);");
                    if (V("Mode") == "上电启动")

                    {
                        init.AppendLine($"    SX_{unit}_Start();");
                    }
                }
                else
                {
                    var n = unit[^1];
                    var plus = V("Plus") == "PA1" ? 1 : 2;
                    var minus = V("Minus") switch
                    {
                        "PA4" => 1,
                        "PA0" or "PA2" => 2,
                        "PA5" => 3,
                        "¼ VREF" => 4,
                        "½ VREF" => 5,
                        "¾ VREF" => 6,
                        _ => 7
                    };
                    init.AppendLine($"    CMP_SetPsel{n}(CMP0, CMP_PSEL_CHNL{plus});\n    CMP_SetMsel{n}(CMP0, CMP_MSEL_CHNL{minus});\n    CMP_Enable{n}(CMP0);");
                }
            }
            if (name == "EXTIRQ")
            {
                var local = unit.StartsWith("LOCAL");
                Ag32InterruptCode.Callback(api, src, $"void SX_{unit}_Interrupt(void)", "处理并解除外部源的有效电平；默认回调先屏蔽该源。", local ? $"INT_DisableIntLocal({unit}_IRQn);" : $"INT_DisableIRQ({unit}_IRQn);");
                src.AppendLine($"void {unit}_isr(void) {{ SX_{unit}_Interrupt(); }}");
                init.AppendLine(local ? $"    INT_EnableIntLocal({unit}_IRQn);\n#ifndef STUDIOX_FREERTOS\n    INT_EnableIntGlobal();\n#endif" :
                    Ag32InterruptCode.Enable(unit, Number(unit, "Priority"), "    "));
            }
            if (name == "DMA")

            {
                GenerateDma(unit, timers, api, src, init);
            }
            if (name == "IWDG")
            {
                var freeze = V("Freeze") switch
                {
                    "Stop" => "IWDG_STOP_FREEZE",
                    "Standby" => "IWDG_STANDBY_FREEZE",
                    "Stop 和 Standby" => "IWDG_FREEZE_ALL",
                    _ => "0u"
                };
                api.AppendLine("/* 定期调用以重装固定 12 位计数器。返回 false 表示备份域写入超时。 */\nbool SX_IWDG_Reload(void);\nbool SX_IWDG_IsReady(void);");
                src.AppendLine("""
                    static bool iwdg_ready;
                    static bool iwdg_wait(void)
                    {
                        const uint32_t start = read_csr(mcycle);
                        while (!(RTC->CRL & RTC_FLAG_RTOFF))
                            if ((uint32_t)(read_csr(mcycle) - start) >= StudioX_SystemClockHz()) return false;
                        return true;
                    }
                    bool SX_IWDG_IsReady(void) { return iwdg_ready; }
                    bool SX_IWDG_Reload(void)
                    {
                        if (!iwdg_ready || !iwdg_wait()) return false;
                        RTC->IWDG = (RTC->IWDG & ~IWDG_RELOAD_MASK) | IWDG_RELOAD_KEY;
                        return iwdg_wait();
                    }
                    """);
                init.AppendLine("    iwdg_ready = false;");
                if (V("Clock") == "LSE")

                {
                    init.AppendLine("""
                        if (!iwdg_wait()) return;
                        RTC->BDCR |= RTC_BDCR_LSEON;
                        const uint32_t start = read_csr(mcycle);
                        while (!(RTC->BDCR & RTC_BDCR_LSERDY))
                            if ((uint32_t)(read_csr(mcycle) - start) >= StudioX_SystemClockHz()) return;
                    """);
                }
                init.AppendLine("    if (!iwdg_wait()) return;")
                    .AppendLine($"    RTC->IWDG = IWDG_CLK_SOURCE_{V("Clock")} | IWDG_CLK_DIV{V("Divider")} | {freeze} | IWDG_RELOAD_KEY | IWDG_ENABLE;")
                    .AppendLine("    iwdg_ready = iwdg_wait();");
            }
            if (name == "POWER")
            {
                for (int i = 0; i < 8; i++)
                {
                    var edge = V("EXT" + i) switch
                    {
                        "上升沿" => "RISE",
                        "下降沿" => "FALL",
                        "双边沿" => "BOTH",
                        _ => "NONE"
                    };
                    init.AppendLine($"    SYS_SetWakeupEdgeByID(SYS_WAKEUP_EXT_INT{i}_ID, SYS_WAKEUP_EDGE_{edge});");
                }
                init.AppendLine($"    SYS_SetAlarmWakeupEdge(SYS_WAKEUP_EDGE_{(V("Alarm") == "关闭" ? "NONE" : "RISE")});");
                api.AppendLine("/* 在应用已准备休眠时调用。Standby 唤醒后将从复位入口启动。 */\nbool SX_POWER_EnterStop(void);\nbool SX_POWER_EnterStandby(void);");
                src.AppendLine("""
                    static bool power_wait_clock(uint32_t ready)
                    {
                        const uint32_t start = read_csr(mcycle);
                        while (!(SYS->CLK_CNTL & ready))
                            if ((uint32_t)(read_csr(mcycle) - start) >= SYS_Clocks.HSI_FREQUENCY) return false;
                        return true;
                    }
                    /* SDK 进入 Stop 时切到 HSI 并关闭 PLL/HSE。唤醒后恢复入睡前的时钟源。
                     * 返回 false 时保持 HSI，应用可延后恢复依赖原时钟的通信。 */
                    bool SX_POWER_EnterStop(void)
                    {
                        const uint32_t previous = SYS->CLK_CNTL;
                        SYS_EnterStopMode();
                        if (previous & SYS_CLK_HSE_ON)
                        {
                            SYS->CLK_CNTL |= SYS_CLK_HSE_ON;
                            if (!power_wait_clock(SYS_CLK_HSE_RDY)) return false;
                        }
                        if (previous & SYS_CLK_PLL_ON)
                        {
                            SYS->CLK_CNTL |= SYS_CLK_PLL_ON;
                            if (!power_wait_clock(SYS_CLK_PLL_RDY)) return false;
                        }
                        SYS_SetClkSource((SYS_ClkSourceTypeDef)(previous & SYS_CLK_SOURCE_MASK));
                        return true;
                    }
                    """);
                var schedule = V("Standby").Contains("RTC");
                if (schedule)

                {
                    src.AppendLine("#include \"RTC.h\"").AppendLine(BackupWait).AppendLine("""
                        static uint32_t power_rtc_counter(void)
                        {
                            uint16_t hi, lo;
                            do { hi = RTC->CNTH; lo = RTC->CNTL; } while (hi != RTC->CNTH);
                            return ((uint32_t)hi << 16) | lo;
                        }
                        """);
                }
                src.AppendLine("bool SX_POWER_EnterStandby(void)\n{");
                if (schedule)

                {
                    src.AppendLine($$"""
                            if (!SX_RTC_IsReady() || !backup_wait()) return false;
                            const uint32_t alarm = power_rtc_counter() + {{Number(unit,"AlarmDelay")}}u;
                            RTC->ALRH = alarm >> 16;
                            if (!backup_wait()) return false;
                            RTC->ALRL = (uint16_t)alarm;
                            if (!backup_wait()) return false;
                            RTC_ClearFlag(RTC_FLAG_ALR);
                            if ((int32_t)(alarm - power_rtc_counter()) <= 0) return false;
                        """);
                }
                src.AppendLine("    SYS_EnterStandbyMode();\n    return true;\n}");
            }
            if (name == "USB")

            {
                GenerateUsb(api, src, init);
            }
            if (name == "ETH")

            {
                GenerateEthernet(api, src, init);
            }
            if (unit != "USB0" && IrqEvents(unit).Length > 0)
            {
                GenerateControllerIrq(unit, api, src, init);
            }
        }
        if (modules.TryGetValue("DMA", out var dma))
        {
            GenerateDmaVectors(dma.Api, dma.Src, dma.Init, spiDma);
        }
        foreach (var (name, m) in modules)

        {
            Ag32PeripheralModule.All.Single(p => p.Name == name).Render(files, m.Api.ToString(), m.Src.ToString(), m.Init.ToString());
        }
    }

}
