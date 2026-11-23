namespace StudioX.Engine;

using System.Text;

internal static class Ag32SpiDma
{
    internal static bool Enabled(Ag32BusSettings bus) => bus.RxDmaChannel.HasValue || bus.TxDmaChannel.HasValue;

    internal static void Generate(Ag32BusSettings[] settings, StringBuilder api, StringBuilder source, StringBuilder init, Ag32Setup setup)
    {
        var ports = settings.Where(Enabled).ToArray();
        if (ports.Length == 0)
        {
            return;
        }
        init.AppendLine("    SYS_EnableAHBClock(AHB_MASK_DMAC0);")
            .AppendLine("    DMAC->Configuration |= ENABLE;");
        var dispatch = new StringBuilder();
        var owned = 0;
        foreach (var bus in ports)
        {
            var port = bus.Module;
            if (!setup.Units.ContainsKey(port))
            {
                source.AppendLine($"static volatile int {port}_channel = -1;");
            }
            source.AppendLine()
                .AppendLine($"static volatile bool {port}_dma_done, {port}_wire_done;");
            Ag32InterruptCode.Callback(api, source, $"void SX_{port}_DmaComplete(uint8_t channel)",
                "DMA 和 SPI 总线传输均已完成，缓冲区可复用。", "(void)channel;");
            Ag32InterruptCode.Callback(api, source, $"void SX_{port}_DmaError(uint8_t channel)",
                "DMA 或 SPI 出错，当前传输已终止，请检查后重试。", "(void)channel;");
            if (bus.InterruptMask != 0)

            {
                Ag32InterruptCode.Callback(api, source, $"void SX_{port}_Interrupt(uint32_t status)",
                                    "普通 SPI 传输完成。DMA 传输使用单独的完成和错误回调。", "(void)status;");
            }
            source.AppendLine($$"""
                static void {{port}}_finish(bool error)
                {
                    const unsigned long irq = clear_csr(mstatus, MSTATUS_MIE);
                    const int ch = {{port}}_channel;
                    if (ch < 0 || (!error && !({{port}}_dma_done && {{port}}_wire_done)))
                    {
                        if (irq & MSTATUS_MIE) INT_EnableIntGlobal();
                        return;
                    }
                    {{port}}_channel = -1;
                    DMAC_DisableChannel((DMAC_ChannelNumTypeDef)ch);
                    SPI_DisableInt({{port}});
                    if (error)
                    {
                        SPI_Init({{port}}, SPI_CTRL_SCLK_DIV{{bus.ClockDivider}});
                        {{(bus.BigEndian ? $"{port}->CTRL &= ~SPI_CTRL_ENDIAN;" : "")}}
                        SPI_ClearInt({{port}});
                    }
                    __asm__ volatile ("fence iorw, iorw" ::: "memory");
                    if (irq & MSTATUS_MIE) INT_EnableIntGlobal();
                    if (error) SX_{{port}}_DmaError((uint8_t)ch);
                    else SX_{{port}}_DmaComplete((uint8_t)ch);
                }

                void {{port}}_isr(void)
                {
                    const uint32_t status = {{port}}->CTRL;
                    SPI_ClearInt({{port}});
                    if ({{port}}_channel >= 0)
                    {
                        {{port}}_wire_done = true;
                        {{port}}_finish((status & SPI_CTRL_ERROR) != 0u);
                    }
                    {{(bus.InterruptMask == 0 ? "" : $"else SX_{port}_Interrupt(status);")}}
                }
                """);
            // SPI 收发共用移位器。保存原全局中断状态，避免任务切换或 ISR 在通道登记与启动之间抢占。
            foreach (var (channel, rx) in new[] { (bus.RxDmaChannel, true), (bus.TxDmaChannel, false) })
            {
                if (channel is not int ch)
                {
                    continue;
                }
                owned |= 1 << ch;
                var name = $"SX_{port}_" + (rx ? "ReceiveDma" : "SendDma");
                var signature = rx ? $"bool {name}(uint32_t command, uint8_t command_bytes, uint32_t *data, uint16_t bytes)"
                    : $"bool {name}(const uint32_t *data, uint16_t bytes)";
                api.AppendLine(rx ? "/* 先发 command 的 1～4 字节，再收 1～4092 字节。 */" : "/* DMA 发送 5～4092 字节。 */")
                    .AppendLine("/* data 按 4 字节对齐，容量向上取整到整字，保持有效直到回调。期间请勿调用同一 SPI 的其他传输接口。参数无效或 DMA 繁忙返回 false。 */")
                    .AppendLine(signature + ";");
                var start = rx ? $$"""
                        SPI_SetPhaseCtrl({{port}}, SPI_PHASE_0, SPI_PHASE_ACTION_TX, SPI_PHASE_MODE_SINGLE, command_bytes);
                        SPI_SetPhaseData({{port}}, SPI_PHASE_0, command);
                        const int phase = SPI_RX_PIPELINE ? SPI_PHASE_2 : SPI_PHASE_1;
                        if (SPI_RX_PIPELINE) SPI_SetPhaseCtrl({{port}}, SPI_PHASE_1, SPI_PHASE_ACTION_DUMMY_TX, SPI_PHASE_MODE_QUAD, 1);
                        SPI_SetPhaseCtrl({{port}}, phase, SPI_PHASE_ACTION_RX, SPI_PHASE_MODE_SINGLE, bytes);
                        DMAC_Config(channel, (uint32_t)&{{port}}->PHASE_DATA[phase], (uint32_t)data,
                                    DMAC_ADDR_INCR_OFF, DMAC_ADDR_INCR_ON, DMAC_WIDTH_32_BIT, DMAC_WIDTH_32_BIT,
                                    DMAC_BURST_1, DMAC_BURST_1, 0, DMAC_PERIPHERAL_TO_MEM_PERIPHERAL_CTRL, SPI_RX_DMA_REQ({{port}}), 0);
                        SPI_Start({{port}}, SPI_RX_PIPELINE ? SPI_CTRL_PHASE_CNT3 : SPI_CTRL_PHASE_CNT2, SPI_CTRL_DMA_ON, SPI_INTERRUPT_ON);
                    """ : $$"""
                        SPI_SetPhaseCtrl({{port}}, SPI_PHASE_0, SPI_PHASE_ACTION_TX, SPI_PHASE_MODE_SINGLE, 4);
                        SPI_SetPhaseData({{port}}, SPI_PHASE_0, data[0]);
                        SPI_SetPhaseCtrl({{port}}, SPI_PHASE_1, SPI_PHASE_ACTION_TX, SPI_PHASE_MODE_SINGLE, bytes - 4u);
                        DMAC_Config(channel, (uint32_t)(data + 1), (uint32_t)&{{port}}->PHASE_DATA[SPI_PHASE_1],
                                    DMAC_ADDR_INCR_ON, DMAC_ADDR_INCR_OFF, DMAC_WIDTH_32_BIT, DMAC_WIDTH_32_BIT,
                                    DMAC_BURST_1, DMAC_BURST_1, 0, DMAC_MEM_TO_PERIPHERAL_PERIPHERAL_CTRL, 0, SPI_TX_DMA_REQ({{port}}));
                        SPI_Start({{port}}, SPI_CTRL_PHASE_CNT2, SPI_CTRL_DMA_ON, SPI_INTERRUPT_ON);
                    """;
                source.AppendLine($$"""
                    {{signature}}
                    {
                        const DMAC_ChannelNumTypeDef channel = DMAC_CHANNEL{{ch}};
                        if (!data || ((uintptr_t)data & 3u) || bytes > SPI_PHASE_BYTE_CNT_MAX ||
                            {{(rx ? "bytes == 0 || command_bytes < 1 || command_bytes > 4" : "bytes <= 4")}}) return false;
                        const unsigned long irq = clear_csr(mstatus, MSTATUS_MIE);
                        const bool available = {{(setup.Units.ContainsKey(port) ? $"!{port}_profile_busy && " : "")}}{{port}}_channel < 0 && !DMAC_IsEnabledChannel(channel);
                        if (available)
                        {
                            {{port}}_dma_done = {{port}}_wire_done = false;
                            {{port}}_channel = channel;
                            SPI_ClearInt({{port}});
                            __asm__ volatile ("fence iorw, iorw" ::: "memory");
                    {{"    " + start.ReplaceLineEndings("\n").Replace("\n", "\n    ", StringComparison.Ordinal)}}
                        }
                        if (irq & MSTATUS_MIE) INT_EnableIntGlobal();
                        return available;
                    }
                    """);
            }
            dispatch.AppendLine($$"""
                    if ({{port}}_channel >= 0)
                    {
                        const uint32_t bit = 1u << {{port}}_channel;
                        if ((complete | errors) & bit)
                        {
                            {{port}}_dma_done = (complete & bit) != 0u;
                            {{port}}_finish((errors & bit) != 0u);
                        }
                    }
                """);
            init.AppendLine($"    SPI_ClearInt({port});").AppendLine(Ag32InterruptCode.Enable(port, bus.InterruptPriority, "    "));
        }
        Ag32InterruptCode.Callback(api, source, "void SX_DMA_OtherChannels(uint32_t complete, uint32_t errors)",
            "同一 DMA 控制器上由应用管理的通道中断。位图是清除前的状态。", "(void)complete; (void)errors;");
        var general = setup.DmaInterruptChannels().Sum(u => 1 << (u[^1] - '0'));
        if (general != 0)
        {
            source.AppendLine("#include \"DMA.h\"");
        }
        source.AppendLine($$"""
            void DMAC0_INTR_isr(void)
            {
                const uint32_t complete = DMAC_IsMaskedIntTCActive();
                const uint32_t errors = DMAC_IsMaskedIntErrorActive();
                DMAC->IntTCClear = complete & ~0x{{general:X}}u;
                DMAC->IntErrorClear = errors & ~0x{{general:X}}u;
                {{(general == 0 ? "" : "SX_DMA_Dispatch(complete, errors);")}}
            {{dispatch}}
                if ((complete | errors) & ~0x{{owned | general:X}}u)
                    SX_DMA_OtherChannels(complete & ~0x{{owned | general:X}}u, errors & ~0x{{owned | general:X}}u);
            }
            """);
        init.AppendLine(Ag32InterruptCode.Enable("DMAC0_INTR", ports[0].DmaPriority, "    "));
    }
}
