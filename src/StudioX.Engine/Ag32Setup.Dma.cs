namespace StudioX.Engine;

using System.Text;

public sealed partial class Ag32Setup
{
    // 每个 LLI 最多搬运 4092 个元素；超过单个描述符的逻辑段由生成器拆分。
    private const int DmaDescriptorElements = 4092;
    private static int DmaDescriptorCount(int elements) => (elements - 1) / DmaDescriptorElements + 1;

    private int[] DmaSegmentSizes(string unit)
    {
        var segments = Value(unit, "SegmentSizes").Split([',', '，'], StringSplitOptions.TrimEntries);
        if (segments.Length > 32)
        {
            throw Error($"{unit} 最多支持 32 段缓冲区。");
        }
        int[] sizes = new int[segments.Length];
        for (int i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out sizes[i]) ||
                sizes[i] < 1 || sizes[i] > 32 * DmaDescriptorElements)

            {
                throw Error($"{unit} 第 {i + 1} 段的元素数应为 1～{32 * DmaDescriptorElements}。");
            }
        }
        if (sizes.Sum(DmaDescriptorCount) > 32)
        {
            throw Error($"{unit} 的分段配置超过 32 个硬件描述符，每个描述符最多容纳 {DmaDescriptorElements} 个元素。");
        }
        return sizes;
    }

    public (int Segments, int Elements, int Descriptors) GetDmaSegmentUsage(string unit)
    {
        var sizes = DmaSegmentSizes(unit);
        return (sizes.Length, sizes.Sum(), sizes.Sum(DmaDescriptorCount));
    }

    private void GenerateDma(string unit, Ag32TimerSettings[] timers, StringBuilder api, StringBuilder src, StringBuilder init)
    {
        var ch = unit[^1] - '0';
        var q = Value(unit, "Request");
        var port = q.Split('_')[0];
        var uart = q.StartsWith("UART");
        var adc = q.StartsWith("ADC");
        var dac = q.StartsWith("DAC");
        var cc = q.Contains("_CC");
        var capture = cc && timers.Single(t => t.Module == port).Channels[q[^1] - '0']!.Edge != null;
        var rx = adc || q.EndsWith("_RX") || capture;
        bool memory = q == "MEMORY";
        var width = memory ? Number(unit, "Width") : uart ? 8 : 32;
        var type = $"uint{width}_t";
        var reg = uart ? "DR" : adc || dac ? "DATA" : cc ? "CCR" + q[^1] : q.EndsWith("_UPDATE") ? "ARR" : Value(unit, "Target");
        var request = adc || dac ? q + "_DMA_REQ" : cc ? port + "_CC_DMA" + q[^1] + "_REQ" : q + "_DMA_REQ";
        var eventName = cc ? "CC" + q[^1] : q.EndsWith("_COM") ? "Com" : q.EndsWith("_TRIGGER") ? "Trigger" : "Update";
        var start = memory ? "" : uart ? $"{port}->DMACR |= UART_DMA_{(rx ? "RX" : "TX")}_EN;" : dac ? $"DAC_{(Value(port, "Buffer") == "启用" ? "Enable" : "EnableNoBuf")}({port}); DAC_EnableDma({port}, {Number(port, "Divider")}u);" : adc ? $"ADC_StartDma({port}, {Number(port, "Divider")}u);" : $"GPTIMER_EnableDma{eventName}({port});";
        var stop = memory ? "" : uart ? $"{port}->DMACR &= ~UART_DMA_{(rx ? "RX" : "TX")}_EN;" : dac ? $"DAC_DisableDma({port});" : adc ? $"ADC_Stop({port});" : $"GPTIMER_DisableDma{eventName}({port});";
        var blocks = Number(unit, "Blocks");
        var irq = Value(unit, "Notify") == "中断";
        var args = memory ? $"const {type} *source, {type} *buffer" : $"{(rx ? "" : "const ")}{type} *buffer";
        var signature = $"bool SX_{unit}_Start({args}, uint32_t count)";
        var srcAddr = memory ? "segments[i].source" : rx ? $"&{port}->{reg}" : "segments[i].buffer";
        var dstAddr = memory ? "segments[i].destination" : rx ? "segments[i].buffer" : $"&{port}->{reg}";
        int[] sizes = Value(unit, "Layout") == "分段缓冲区" ? DmaSegmentSizes(unit) : [];
        int capacity = Math.Max(blocks, sizes.Sum(DmaDescriptorCount));
        string bufferType = $"SX_{unit}_Buffer";
        string fields = memory ? $"const {type} *source; {type} *destination;" : $"{(rx ? "" : "const ")}{type} *buffer;";
        string segmentsApi = $"bool SX_{unit}_StartSegments(const {bufferType} *segments, uint32_t segment_count)";
        var flow = memory ? "MEM_TO_MEM" : rx ? "PERIPHERAL_TO_MEM" : "MEM_TO_PERIPHERAL";
        var cfg = $"DMAC_MakeConfiguration(DMAC_{flow}_DMA_CTRL, {(rx ? request : "0")}, {(rx || memory ? "0" : request)})";
        if (!irq)

        {
            cfg += " & ~((1u << DMAC_INT_ERROR_MASK_OFFSET) | (1u << DMAC_INT_TC_MASK_OFFSET))";
        }
        api.AppendLine($"/* {q}，count 为 {width} 位元素数，最多 {blocks} × 4092 个。传输结束前缓冲区由 DMA 占用。 */\n{signature};")
            .AppendLine($"/* 0 传输中，1 完成，-1 错误，-2 空闲。 */\nint SX_{unit}_Poll(void);");
        (sizes.Length == 0 ? src : api).AppendLine($"typedef struct {{ {fields} }} {bufferType};");
        if (sizes.Length != 0)
        {
            api.AppendLine($"#define SX_{unit}_SEGMENT_COUNT {sizes.Length}u")
               .AppendLine($"/* 各段元素数依次为 {string.Join(", ", sizes)}。传入相同数量的缓冲区，按顺序搬运，整链完成后再复用。 */")
               .AppendLine(segmentsApi + ";");
        }
        if (init.Length == 0)

        {
            init.AppendLine("    SYS_EnableAHBClock(AHB_MASK_DMAC0);\n    DMAC->Configuration |= ENABLE;");
        }
        if (irq)
        {
            Ag32InterruptCode.Callback(api, src, $"void SX_{unit}_Complete(void)", "传输链的最后一段已完成。");
            Ag32InterruptCode.Callback(api, src, $"void SX_{unit}_Error(void)", "传输出错，缓冲区可能仅部分更新。");
        }
        src.AppendLine($$"""
            static volatile int state_{{unit}} = -2;
            static DMAC_LLI_TypeDef chain_{{unit}}[{{capacity}}];
            static bool start_{{unit}}(const {{bufferType}} *segments, const uint32_t *sizes, uint32_t segment_count)
            {
                /* 启动前检查所有缓冲区，避免后续段无效时留下半次传输。 */
                for (uint32_t i = 0; i < segment_count; ++i)
                {
                    const uintptr_t bytes = sizes[i] * sizeof({{type}});
                    {{(!rx ? $"const uintptr_t src = (uintptr_t){srcAddr};\n        if (!src || src % sizeof({type}) || src > UINTPTR_MAX - (bytes - 1u)) return false;" : "")}}
                    {{(rx || memory ? $"const uintptr_t dst = (uintptr_t){dstAddr};\n        if (!dst || dst % sizeof({type}) || dst > UINTPTR_MAX - (bytes - 1u)) return false;" : "")}}
                    {{(memory ? "if ((src > dst ? src - dst : dst - src) < bytes) return false;" : "")}}
                }
                const unsigned long irq = clear_csr(mstatus, MSTATUS_MIE);
                const DMAC_ChannelNumTypeDef ch = DMAC_CHANNEL{{ch}};
                const bool available = state_{{unit}} != 0 && !DMAC_IsEnabledChannel(ch);
                if (available)
                {
                    state_{{unit}} = 0;
                    DMAC_ClearChannelIntTC(ch);
                    DMAC_ClearChannelIntError(ch);
                    unsigned descriptor = 0;
                    for (uint32_t i = 0; i < segment_count; ++i)
                    {
                        uint32_t source_address = (uint32_t)(uintptr_t){{srcAddr}};
                        uint32_t destination = (uint32_t)(uintptr_t){{dstAddr}};
                        uint32_t remaining = sizes[i];
                        while (remaining)
                        {
                            uint32_t n = remaining > DMAC_MAX_TRANSFER_SIZE ? DMAC_MAX_TRANSFER_SIZE : remaining;
                            remaining -= n;
                            bool last = !remaining && i + 1u == segment_count;
                            DMAC_LLI_TypeDef *d = &chain_{{unit}}[descriptor++];
                            d->SrcAddr = source_address;
                            d->DstAddr = destination;
                            d->LLI = last ? 0u : (uint32_t)(uintptr_t)(d + 1);
                            d->Control = DMAC_MakeControl(DMAC_ADDR_INCR_{{(!rx ? "ON" : "OFF")}}, DMAC_ADDR_INCR_{{(rx || memory ? "ON" : "OFF")}},
                                DMAC_WIDTH_{{width}}_BIT, DMAC_WIDTH_{{width}}_BIT, DMAC_BURST_{{Value(unit,"SrcBurst")}}, DMAC_BURST_{{Value(unit,"DstBurst")}}, n, last ? ENABLE : DISABLE);
                            {{(!rx ? $"source_address += n * sizeof({type});" : "")}}
                            {{(rx || memory ? $"destination += n * sizeof({type});" : "")}}
                        }
                    }
                    DMAC_SetSrcAddress(ch,chain_{{unit}}[0].SrcAddr);
                    DMAC_SetDstAddress(ch,chain_{{unit}}[0].DstAddr);
                    DMAC_SetLLI(ch,chain_{{unit}}[0].LLI);
                    DMAC_SetControl(ch,chain_{{unit}}[0].Control);
                    __asm__ volatile ("fence iorw, iorw" ::: "memory");
                    DMAC_SetConfiguration(ch, {{cfg}});
                    {{start}}
                }
                if (irq & MSTATUS_MIE) INT_EnableIntGlobal();
                return available;
            }
            {{signature}}
            {
                if (!count || count > {{blocks}}u * DMAC_MAX_TRANSFER_SIZE) return false;
                const {{bufferType}} segment = { {{(memory ? "source, buffer" : "buffer")}} };
                return start_{{unit}}(&segment, &count, 1u);
            }
            """);
        if (sizes.Length != 0)

        {
            src.AppendLine($$"""
                {{segmentsApi}}
                {
                    static const uint32_t sizes[] = { {{string.Join(", ", sizes.Select(n => n + "u"))}} };
                    if (!segments || segment_count != SX_{{unit}}_SEGMENT_COUNT) return false;
                    return start_{{unit}}(segments, sizes, segment_count);
                }
                """);
        }
        src.AppendLine($$"""
            static void finish_{{unit}}(int status)
            {
                {{stop}}
                DMAC_DisableChannel(DMAC_CHANNEL{{ch}});
                DMAC_ClearChannelIntTC(DMAC_CHANNEL{{ch}});
                DMAC_ClearChannelIntError(DMAC_CHANNEL{{ch}});
                __asm__ volatile ("fence iorw, iorw" ::: "memory");
                state_{{unit}} = status;
            }
            int SX_{{unit}}_Poll(void)
            {
                const unsigned long irq = clear_csr(mstatus, MSTATUS_MIE);
                {{(irq ? "/* 完成状态由中断锁存。 */" : $"if (state_{unit} == 0) {{\n        if (DMAC_IsRawIntErrorActive() & (1u << {ch})) finish_{unit}(-1);\n        else if (DMAC_IsRawIntTCActive() & (1u << {ch})) finish_{unit}(1);\n    }}")}}
                int result = state_{{unit}};
                if (irq & MSTATUS_MIE) INT_EnableIntGlobal();
                return result;
            }
            """);
    }

    internal string[] DmaInterruptChannels() => Units.Keys.Where(u => u.StartsWith("DMA_CH") && Value(u, "Notify") == "中断").Order().ToArray();

    private void GenerateDmaVectors(StringBuilder api, StringBuilder src, StringBuilder init, bool spiDma)
    {
        var channels = DmaInterruptChannels();
        if (channels.Length == 0)

        {
            return;
        }
        api.AppendLine("/* DMA 向量分发入口。 */\nvoid SX_DMA_Dispatch(uint32_t complete,uint32_t errors);");
        src.AppendLine("void SX_DMA_Dispatch(uint32_t complete,uint32_t errors)\n{");
        foreach (var unit in channels)
        {
            var ch = unit[^1];
            src.AppendLine($"    if ((complete | errors) & (1u << {ch})) {{")
                .AppendLine($"        bool error = (errors & (1u << {ch})) != 0;")
                .AppendLine($"        finish_{unit}(error ? -1 : 1);")
                .AppendLine($"        if (error) SX_{unit}_Error(); else SX_{unit}_Complete();\n    }}");
        }
        src.AppendLine("}");
        if (spiDma)

        {
            src.AppendLine("extern void DMAC0_INTR_isr(void);");
        }
        foreach (var (vector, priority) in new[] { ("DMAC0_INTTC", "Complete"), ("DMAC0_INTERR", "Error") })
        {
            var dispatch = spiDma ? "DMAC0_INTR_isr();" : "SX_DMA_Dispatch(DMAC_IsMaskedIntTCActive(), DMAC_IsMaskedIntErrorActive());";
            src.AppendLine($"void {vector}_isr(void) {{ {dispatch} }}");
            init.AppendLine(Ag32InterruptCode.Enable(vector, Number("DMA_IRQ", priority), "    "));
        }
    }
}
