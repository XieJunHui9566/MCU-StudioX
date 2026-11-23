namespace StudioX.Engine;

using System.Text;

public static class Ag32BusInterrupts
{
    private static readonly Ag32BusInterruptEvent[] Can = [
        new(1, "接收帧", "CAN_IR_RI"), new(2, "发送完成", "CAN_IR_TI"),
        new(4, "错误告警", "CAN_IR_EI"), new(8, "接收溢出", "CAN_IR_DOI"),
        new(16, "唤醒", "CAN_IR_WUI"), new(32, "错误被动状态变化", "CAN_IR_EPI"),
        new(64, "仲裁丢失", "CAN_IR_ALI"), new(128, "总线错误", "CAN_IR_BEI")];

    public static IReadOnlyList<Ag32BusInterruptEvent> Events(string module) => module switch
    {
        "CAN0" => Can,
        "SPI0" or "SPI1" => [new(1, "传输完成", "SPI_CTRL_INT_EN")],
        "I2C0" or "I2C1" => [new(1, "字节传输完成 / 仲裁丢失", "I2C_SR_IF")],
        _ => []
    };

    internal static string CanMask(int mask) => string.Join(" | ", Can.Where(e => (e.Mask & mask) != 0).Select(e => e.Flag));

    internal static void Generate(Ag32BusSettings bus, StringBuilder api, StringBuilder source, Ag32Setup? setup = null)
    {
        if (bus.Module == "CAN0" && bus.FilterMode != "ALL")
        {
            var format = bus.FilterMode.StartsWith("STD", StringComparison.Ordinal) ? "CAN_STANDARD_FRAME_FORMAT" : "CAN_EXTENDTED_FRAME_FORMAT";
            api.AppendLine("/* 轮询接收时核对实际帧格式和 ID。接收中断自动应用相同条件。 */")
                .AppendLine("bool SX_CAN0_MatchesFilter(const CAN_RxMessageTypeDef *frame);");
            var mask = Ag32BusInitialization.EffectiveMask(bus);
            var second = Ag32BusInitialization.SecondFilter(bus, setup);
            var additional = second is { } f ? $" || (frame->ID & 0x{f.Mask:X}u) == 0x{f.Mask & f.Id:X}u" : "";
            source.AppendLine($$"""
                bool SX_CAN0_MatchesFilter(const CAN_RxMessageTypeDef *frame)
                {
                    return frame && frame->FF == {{format}} && ((frame->ID & 0x{{mask:X}}u) == 0x{{bus.FilterId & mask:X}}u{{additional}});
                }
                """);
        }
        if (bus.InterruptMask == 0 || Ag32SpiDma.Enabled(bus))
        {
            return;
        }
        var module = bus.Module;
        if (module == "CAN0")
        {
            CanHandler(bus.InterruptMask, bus.FilterMode != "ALL", api, source);
            return;
        }
        var spi = module.StartsWith("SPI", StringComparison.Ordinal);
        api.AppendLine(spi ? "/* 启动传输时选择 SPI_INTERRUPT_ON。status 为清中断前的 CTRL。 */" :
            "/* status 为应答中断前的 SR，包含应答及仲裁丢失状态。 */");
        Ag32InterruptCode.Callback(api, source, $"void SX_{module}_Interrupt(uint32_t status)",
            spi ? "SPI 传输完成中断。status 是清除中断前的 CTRL，启动传输时选择 SPI_INTERRUPT_ON。"
                : "I²C 字节传输完成或仲裁丢失时调用。status 是应答中断前的 SR。", "(void)status;");
        var family = module[..^1];
        source.AppendLine($"void {module}_isr(void)").AppendLine("{")
            .AppendLine($"    uint32_t status = {module}->{(spi ? "CTRL" : "SR")};")
            .AppendLine($"    {family}_ClearInt({module});")
            .AppendLine($"    SX_{module}_Interrupt(status);").AppendLine("}");
    }

    private static void CanHandler(int mask, bool filtered, StringBuilder api, StringBuilder source)
    {
        var rx = (mask & 1) != 0;
        api.AppendLine("/* events 为 CAN_IR_* 位。frame 仅在本次接收回调内有效，其余事件为 NULL。 */")
            .AppendLine("/* arbitration 和 error 分别是仲裁丢失与总线错误时的 ALC 和 ECC 快照。 */");
        Ag32InterruptCode.Callback(api, source, "void SX_CAN0_Interrupt(uint32_t events, const CAN_RxMessageTypeDef *frame, uint32_t arbitration, uint32_t error)",
            rx ? "CAN 中断。events 标明触发事件，收到的 frame 仅在本次回调内有效。"
                : "CAN 中断。events 标明触发事件，当前配置下 frame 为 NULL。",
            "(void)events; (void)frame; (void)arbitration; (void)error;");
        source.AppendLine("void CAN0_isr(void)").AppendLine("{")
            .AppendLine($"    uint32_t events = CAN_GetIntStatus(CAN0) & CAN0->IER & ({CanMask(mask)});");
        // 每个锁存寄存器只读取一次，释放硬件捕获并将本次状态交给用户。
        if ((mask & 64) != 0)
        {
            source.AppendLine("    const uint32_t arbitration = (events & CAN_IR_ALI) ? CAN0->ALC : 0u;");
        }
        if ((mask & 128) != 0)
        {
            source.AppendLine("    const uint32_t error = (events & CAN_IR_BEI) ? CAN0->ECC : 0u;");
        }
        if ((mask & 8) != 0)
        {
            source.AppendLine("    if (events & CAN_IR_DOI) CAN_ClearDataOverrun(CAN0);");
        }
        if (rx)
        {
            // SDK CAN_Receive 等待就绪并按原始 DLC 拷贝。ISR 每次只处理一个经典 CAN 帧，最多八字节。
            source.AppendLine("""
                    CAN_RxMessageTypeDef frame = {0};
                    bool received = (events & CAN_IR_RI) && (CAN0->SR & CAN_SR_RBS);
                    if (received)
                    {
                        uint32_t info = CAN0->RXFRAME;
                        frame.FF = info & CAN_FRAME_FF;
                        frame.RTR = info & CAN_FRAME_RTR;
                        uint32_t dlc = info & CAN_FRAME_DLC;
                        frame.DLC = dlc > 8u ? 8u : dlc;
                        uint32_t offset;
                        if (frame.FF == CAN_STANDARD_FRAME_FORMAT)
                        {
                            frame.ID = (CAN0->RXDATA[0] << 3) | (CAN0->RXDATA[1] >> 5);
                            offset = 2u;
                        }
                        else
                        {
                            frame.ID = (CAN0->RXDATA[0] << 21) | (CAN0->RXDATA[1] << 13) |
                                       (CAN0->RXDATA[2] << 5) | (CAN0->RXDATA[3] >> 3);
                            offset = 4u;
                        }
                        if (frame.RTR != CAN_REMOTE_FRAME)
                            for (uint32_t i = 0; i < frame.DLC; ++i) frame.Data[i] = CAN0->RXDATA[offset + i];
                        CAN_ReleaseRxBuffer(CAN0);
                    }
                """);
            if (filtered)

            {
                source.AppendLine("    if (received && !SX_CAN0_MatchesFilter(&frame)) { received = false; events &= ~CAN_IR_RI; }");
            }
        }
        var frame = rx ? "received ? &frame : NULL" : "NULL";
        var arbitration = (mask & 64) != 0 ? "arbitration" : "0u";
        var error = (mask & 128) != 0 ? "error" : "0u";
        source.AppendLine($"    if (events) SX_CAN0_Interrupt(events, {frame}, {arbitration}, {error});");
        source.AppendLine("}");
    }
}
