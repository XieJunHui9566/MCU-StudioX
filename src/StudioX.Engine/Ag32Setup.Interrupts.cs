namespace StudioX.Engine;

using System.Text;

public sealed partial class Ag32Setup
{
    public static Ag32BusInterruptEvent[] UsbEvents(string role) =>
        IrqEvents("USB0").Where(e => role == "Host" ? (e.Mask & (64 | 256)) == 0 : (e.Mask & (8 | 32)) == 0).ToArray();

    public static Ag32BusInterruptEvent[] IrqEvents(string unit) => unit switch
    {
        "USB0" => [new(1,"传输完成","USB_INT_UI"),new(2,"传输错误","USB_INT_UEI"),new(4,"端口变化","USB_INT_PCI"),
            new(8,"帧列表回卷","USB_INT_FRI"),new(16,"系统错误","USB_INT_SEI"),new(32,"异步队列推进","USB_INT_AAI"),
            new(64,"总线复位","USB_INT_URI"),new(128,"帧起始","USB_INT_SRI"),new(256,"挂起","USB_INT_SLI")],
        "MAC0" => [new(1,"接收与接收错误","MAC_STAT_RX_INT | MAC_STAT_RX_ERR | MAC_STAT_RX_AHBERR | MAC_STAT_TOO_SMALL | MAC_STAT_INV_ADDR"),
            new(2,"发送与发送错误","MAC_STAT_TX_INT | MAC_STAT_TX_ERR | MAC_STAT_TX_AHBERR"),new(4,"PHY 状态变化","MAC_STAT_PHY_STAT")],
        "FLASH" => [new(1, "操作完成", "FLASH_SR_EOP"), new(2, "编程或写保护错误", "FLASH_SR_PGERR | FLASH_SR_WRPRTERR")],
        "FCB0" => [new(1, "器件 ID 错误", "FCB_STAT_ERR_ID"), new(2, "配置头错误", "FCB_STAT_ERR_HEADER"), new(4, "CRC 错误", "FCB_STAT_ERR_CRC")],
        _ => []
    };

    public static string ControllerModule(string unit) => unit switch
    {
        "USB0" => "USB",
        "MAC0" => "ETH",
        "FLASH" => "FLASHIRQ",
        "FCB0" => "FCBIRQ",
        "DMAC0_INTTC" or "DMAC0_INTERR" => "DMA",
        "DMAC0_INTR" => "SPI",
        "RTC" => "RTC",
        "WATCHDOG0" => "WDOG",
        _ when unit.Contains("TIMER") => "TIM",
        _ when unit.StartsWith("EXT_INT") || unit.StartsWith("LOCAL_INT") => "EXTIRQ",
        _ => unit[..^1]
    };

    private void GenerateControllerIrq(string unit, StringBuilder api, StringBuilder src, StringBuilder init)
    {
        int selected = Number(unit, "Events");
        if (selected == 0)

        {
            return;
        }
        var mask = string.Join(" | ", IrqEvents(unit).Where(e => (e.Mask & selected) != 0).Select(e => e.Flag));
        var hook = $"SX_{unit}_Interrupt";
        Ag32InterruptCode.Callback(api, src, $"void {hook}(uint32_t events)", "events 为本次已应答的硬件状态位。", "(void)events;");
        string status, clear;
        switch (unit)
        {
            case "USB0":
                status = "USB_GetStatus() & USB->USBINTR";
                clear = "USB_ClearStatus(events);";
                init.AppendLine($"    USB_ClearStatus({mask});\n    USB_SetInt({mask});");
                break;
            case "MAC0":
                status = "MAC_GetStatus(MAC0)";
                clear = "MAC_ClearStatus(MAC0, events);";
                init.AppendLine($"    MAC_ClearStatus(MAC0, {mask});");
                if ((selected & 1) != 0)

                {
                    init.AppendLine("    MAC_EnableIntRx(MAC0);");
                }
                if ((selected & 2) != 0)

                {
                    init.AppendLine("    MAC_EnableIntTx(MAC0);");
                }
                if ((selected & 4) != 0)

                {
                    init.AppendLine("    MAC_EnableIntPhy(MAC0);");
                }
                break;
            case "FCB0":
                status = "FCB->STAT & FCB->INT";
                clear = "FCB->STAT = events;";
                init.AppendLine($"    SYS_EnableAPBClock(APB_MASK_FCB0);\n    FCB->STAT = {mask};\n    FCB_EnableInt({mask});");
                break;
            default:
                status = "FLASH->SR";
                clear = "FLASH->SR = events;";
                api.AppendLine("/* 应用主动解锁 Flash 后、发起操作前调用。返回 false 表示仍锁定。 */\nbool SX_FLASH_ArmInterrupts(void);");
                src.AppendLine("bool SX_FLASH_ArmInterrupts(void)\n{\n    if (FLASH_IsLocked()) return false;")
                    .AppendLine($"    FLASH->SR = {mask};")
                    .AppendLine($"    FLASH->CR |= {((selected & 1) != 0 ? "FLASH_CR_EOPIE" : "0u")} | {((selected & 2) != 0 ? "FLASH_CR_ERRIE" : "0u")};\n    return true;\n}}");
                break;
        }
        src.AppendLine($"void {unit}_isr(void)\n{{\n    uint32_t events = {status} & ({mask});\n    if (events) {{ {clear} {hook}(events); }}\n}}");
        init.AppendLine(Ag32InterruptCode.Enable(unit, Number(unit, "Priority"), "    "));
    }
}
