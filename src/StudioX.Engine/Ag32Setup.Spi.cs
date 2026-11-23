namespace StudioX.Engine;

using System.Text;

public sealed partial class Ag32Setup
{
    internal void GenerateSpiProfiles(StringBuilder api, StringBuilder src, Ag32BusSettings[] buses)
    {
        foreach (var bus in buses.Where(b => b.Module.StartsWith("SPI") && Units.ContainsKey(b.Module)))
        {
            var unit = bus.Module;
            int count = Number(unit, "Phases");
            var signature = $"bool SX_{unit}_Transaction(uint32_t words[{count}], uint32_t timeout_cycles)";
            api.AppendLine($"/* 每阶段一个数据字。TX 提供数据，RX 接收结果；POLL 返回最后采样的字节。超时或占用返回 false。 */\n{signature};");
            var dma = Ag32SpiDma.Enabled(bus);
            if (dma)

            {
                src.AppendLine($"static volatile int {unit}_channel = -1;");
            }
            src.AppendLine($"static volatile bool {unit}_profile_busy;").AppendLine(signature).AppendLine("{")
                .AppendLine("    if (!words || !timeout_cycles) return false;");
            int extra = 0;
            var phases = new StringBuilder();
            var reads = new StringBuilder();
            bool receiving = false;
            for (int i = 1; i <= count; ++i)
            {
                var action = Value(unit, "Action" + i);
                var lines = Value(unit, "Lines" + i) switch
                {
                    "2" => "DUAL",
                    "4" => "QUAD",
                    _ => "SINGLE"
                };
                var bytes = action == "POLL" ? 0 : Number(unit, "Bytes" + i);
                if (!receiving && action is "RX" or "POLL")
                {
                    phases.AppendLine($"    if (SPI_RX_PIPELINE) SPI_SetPhaseCtrl({unit},(SPI_PhaseNumberTypeDef)p++,SPI_PHASE_ACTION_DUMMY_TX,SPI_PHASE_MODE_QUAD,1);");
                    extra++;
                }
                receiving = action is "RX" or "POLL";
                phases.AppendLine($"    SPI_SetPhaseCtrl({unit},(SPI_PhaseNumberTypeDef)p,SPI_PHASE_ACTION_{action},SPI_PHASE_MODE_{lines},{bytes}u);");
                if (action == "POLL")
                {
                    uint poll = (uint)(Number(unit, "Limit" + i) << 24 | Number(unit, "Mask" + i) << 16 | Number(unit, "Expect" + i) << 8);
                    phases.AppendLine($"    SPI_SetPhaseData({unit},(SPI_PhaseNumberTypeDef)p,0x{poll:X8}u);");
                    reads.AppendLine($"        words[{i - 1}] = SPI_GetPhaseData({unit},(SPI_PhaseNumberTypeDef)({i - 1} + (SPI_RX_PIPELINE ? {extra} : 0))) & 0xffu;");
                }
                if (action == "TX")

                {
                    phases.AppendLine($"    SPI_SetPhaseData({unit},(SPI_PhaseNumberTypeDef)p,words[{i - 1}]);");
                }
                if (action == "RX")

                {
                    reads.AppendLine($"        words[{i - 1}] = SPI_GetPhaseData({unit},(SPI_PhaseNumberTypeDef)({i - 1} + (SPI_RX_PIPELINE ? {extra} : 0)));");
                }
                phases.AppendLine("    ++p;");
            }
            if (count + extra > 8)

            {
                src.AppendLine("    if (SPI_RX_PIPELINE) return false; /* 硬件流水线补偿也占用阶段。 */");
            }
            src.AppendLine($$"""
                    unsigned long irq = clear_csr(mstatus, MSTATUS_MIE);
                    const bool available = !{{unit}}_profile_busy{{(dma ? $" && {unit}_channel < 0" : "")}};
                    if (available) {{unit}}_profile_busy = true;
                    if (irq & MSTATUS_MIE) INT_EnableIntGlobal();
                    if (!available) return false;
                    uint32_t saved = {{unit}}->CTRL & (SPI_CTRL_WP_EN | SPI_CTRL_ENDIAN | SPI_CTRL_SCLK_DIV_MASK | SPI_CTRL_INT_EN);
                    unsigned p = 0;
                {{phases}}
                    SPI_Start({{unit}},(SPI_PhaseCntTypeDef)((p-1u) << SPI_CTRL_PHASE_CNT_OFFSET),SPI_CTRL_DMA_OFF,SPI_INTERRUPT_OFF);
                    const uint32_t start = read_csr(mcycle);
                    while (!SPI_IsDone({{unit}}) && !SPI_IsError({{unit}}) && (uint32_t)(read_csr(mcycle)-start) < timeout_cycles) {}
                    const bool ok = SPI_IsDone({{unit}}) && !SPI_IsError({{unit}});
                    if (!ok) SPI_Reset({{unit}});
                    else {
                {{reads}}    }
                    SPI_ClearInt({{unit}});
                    {{unit}}->CTRL = saved;
                    __asm__ volatile ("fence iorw, iorw" ::: "memory");
                    {{unit}}_profile_busy = false;
                    return ok;
                }
                """);
        }
    }
}
