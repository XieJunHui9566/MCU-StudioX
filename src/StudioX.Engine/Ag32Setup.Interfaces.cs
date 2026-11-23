namespace StudioX.Engine;

using System.Text;

public sealed partial class Ag32Setup
{
    private void GenerateUsb(StringBuilder api, StringBuilder src, StringBuilder init)
    {
        bool host = Value("USB0", "Role") == "Host";
        api.AppendLine("bool SX_USB_IsReady(void);");
        src.AppendLine("static bool usb_ready;\nbool SX_USB_IsReady(void) { return usb_ready; }");
        init.AppendLine("""
                usb_ready = false;
                SYS_EnableAHBClock(AHB_MASK_USB0);
                USB_Stop();
                USB->USBCMD |= USB_CMD_RST;
                const uint32_t start = read_csr(mcycle);
                while (USB->USBCMD & USB_CMD_RST)
                    if ((uint32_t)(read_csr(mcycle) - start) >= StudioX_SystemClockHz()) return;
            """);
        if (host)
        {
            api.AppendLine("/* 异步控制传输队列头。板级 VBUS 供电由应用管理。 */\nUSB_QHD *SX_USB_HostQueue(void);");
            src.AppendLine("static USB_QHD host_queue __attribute__((aligned(64)));\nUSB_QHD *SX_USB_HostQueue(void) { return &host_queue; }");
            init.AppendLine("""
                    USB_SetControllerMode(USB_MODE_HOST);
                    USB_PortForceFullSpeed();
                    USB_InitQHD(&host_queue, USB_SETUP_EP, 0, USB_LP_TYP_QH);
                    host_queue.Overlay.Next.Address = USB_TERMINATE;
                    host_queue.Overlay.AltNext.Address = USB_TERMINATE;
                    USB_SetAsyncListAddr((uint32_t)&host_queue);
                    USB_PortEnablePower();
                """);
        }
        else
        {
            api.AppendLine("/* USB 复位事件后重新调用 SX_USB_ResetEndpoints。队列下标为 2*端点号 + IN。 */\nvoid SX_USB_ResetEndpoints(void);\nUSB_DQH *SX_USB_DeviceQueues(void);");
            src.AppendLine("static USB_DQH endpoints[2 * USB_ENDPOINTS] __attribute__((aligned(2048)));\nUSB_DQH *SX_USB_DeviceQueues(void) { return endpoints; }\nvoid SX_USB_ResetEndpoints(void)\n{")
                .AppendLine("    USB_SetEndPointListAddr((uint32_t)endpoints);\n    USB_SetDeviceAddress(0);");
            foreach (var ep in new[] { "0OUT", "0IN" }.Concat(EndpointDirections()))
            {
                int n = ep[0] - '0';
                bool input = ep.EndsWith("IN");
                string dir = input ? "TX" : "RX";
                string type = n == 0 ? "Control" : Value("USB0", "EP" + ep);
                if (type == "关闭")
                {
                    src.AppendLine($"    USB_DisableEndPt({n}, USB_ENDPT_DIR_{dir});");
                    continue;
                }
                var idx = n * 2 + (input ? 1 : 0);
                var code = type switch
                {
                    "Bulk" => "BULK",
                    "Isochronous" => "ISO",
                    "Interrupt" => "INT",
                    _ => "CTRL"
                };
                src.AppendLine($"    USB_InitDQH(&endpoints[{idx}]);\n    endpoints[{idx}].MaxPacketLength = {(n == 0 ? 64 : Number("USB0", "Size" + ep))}u;");
                if (n == 0 && !input)

                {
                    src.AppendLine("    endpoints[0].IntOnSetup = 1;");
                }
                if (type == "Isochronous")

                {
                    src.AppendLine($"    endpoints[{idx}].Mult = 1;");
                }
                src.AppendLine($"    USB_SetEndPtType({n}, USB_ENDPT_DIR_{dir}, USB_ENDPT_TYPE_{code});\n    USB_ResetEndPtDataToggle({n}, USB_ENDPT_DIR_{dir});\n    USB_EnableEndPt({n}, USB_ENDPT_DIR_{dir});");
            }
            src.AppendLine("}");
            init.AppendLine("    USB_SetControllerMode(USB_MODE_DEIVCE);\n    USB_PortForceFullSpeed();\n    SX_USB_ResetEndpoints();");
        }
        GenerateControllerIrq("USB0", api, src, init);
        init.AppendLine((Number("USB0", "Events") == 0 ? "    USB_SetInt(0);" : "") + "\n    __asm__ volatile (\"fence iorw, iorw\" ::: \"memory\");\n    USB_Run();\n    usb_ready = true;");
    }

    private void GenerateEthernet(StringBuilder api, StringBuilder src, StringBuilder init)
    {
        var mode = Value("MAC0", "Mode");
        var negotiate = Value("MAC0", "Link") == "自动协商";
        var addr = Value("MAC0", "Address").Split(':').Select(x => "0x" + x + "u");
        api.AppendLine("/* 返回 NULL 表示参考时钟、控制器或 PHY 初始化失败。应用提交收发缓冲区后启动 RX/TX。 */\nMAC_HandleTypeDef *SX_ETH_Handle(void);");
        src.AppendLine("""
            static MAC_HandleTypeDef eth;
            static MAC_DescriptorTypeDef tx[4] __attribute__((aligned(MAC_DESC_MIN_ALIGN)));
            static MAC_DescriptorTypeDef rx[4] __attribute__((aligned(MAC_DESC_MIN_ALIGN)));
            static bool eth_ready;
            MAC_HandleTypeDef *SX_ETH_Handle(void) { return eth_ready ? &eth : NULL; }
            static bool eth_wait(volatile uint32_t *reg, uint32_t mask)
            {
                const uint32_t start = read_csr(mcycle);
                while (*reg & mask)
                    if ((uint32_t)(read_csr(mcycle) - start) >= StudioX_SystemClockHz()) return false;
                return true;
            }
            static bool eth_mdio(uint8_t reg, uint16_t *data, bool write)
            {
                if (!eth_wait(&MAC0->MDIO, MAC_MDIO_BUSY)) return false;
                MAC0->MDIO = (write ? (uint32_t)*data << MAC_MDIO_DATA_OFFSET : 0u) |
                    ((uint32_t)eth.Init.PhyAddress << MAC_MDIO_PHYADDR_OFFSET) |
                    ((uint32_t)reg << MAC_MDIO_REGADDR_OFFSET) | eth.Init.MdcScaler | (write ? MAC_MDIO_WRITE : MAC_MDIO_READ);
                if (!eth_wait(&MAC0->MDIO, MAC_MDIO_BUSY)) return false;
                if (!write) *data = (uint16_t)(MAC0->MDIO >> MAC_MDIO_DATA_OFFSET);
                return true;
            }
            """);
        if (negotiate)
        {
            api.AppendLine("/* 周期调用。链路和协商完成后按共同能力设置 MAC，断线时返回 false。 */\nbool SX_ETH_RefreshLink(void);");
            src.AppendLine("""
                bool SX_ETH_RefreshLink(void)
                {
                    uint16_t status, local, peer;
                    if (!eth_ready || !eth_mdio(1,&status,false) || !eth_mdio(1,&status,false) ||
                        status == 0xffffu || (status & 0x24u) != 0x24u ||
                        !eth_mdio(4,&local,false) || !eth_mdio(5,&peer,false) ||
                        (local & 31u) != 1u || (peer & 31u) != 1u) return false;
                    unsigned common = local & peer;
                    if (!(common & 0x1e0u)) return false;
                    bool fast = (common & 0x180u) != 0;
                    bool full = fast ? (common & 0x100u) != 0 : (common & 0x40u) != 0;
                    eth.Init.Speed = fast ? MAC_SPEED_100M : MAC_SPEED_10M;
                    eth.Init.DuplexMode = full ? MAC_FULL_DUPLEX : MAC_HALF_DUPLEX;
                    MODIFY_REG(MAC0->CTRL, MAC_CTRL_SPEED | MAC_CTRL_DUPLEX, eth.Init.Speed | eth.Init.DuplexMode);
                    return true;
                }
                """);
        }
        init.AppendLine($$"""
                eth_ready = false;
                SYS_EnableAHBClock(AHB_MASK_MAC0);
                MAC_Reset(MAC0);
                if (!eth_wait(&MAC0->CTRL, MAC_CTRL_RESET)) return;
                MAC_HandleInit(&eth, tx, 4, rx, 4);
                eth.Init.PhyAddress = {{Number("MAC0", "Phy")}}u;
                eth.Init.MediaInterface = MAC_MEDIA_INTERFACE_{{mode}};
                const uint8_t address[] = { {{string.Join(", ", addr)}} };
                memcpy(eth.Init.MacAddress, address, 6);
                uint16_t status = 0;
                if (!eth_mdio(MAC_PHY_REG_STAT, &status, false) || !status || status == 0xffffu) return;
                /* 用图形配置的速率和双工方式统一设置 PHY 与 MAC。 */
                uint16_t control = {{(Value("MAC0", "Speed") == "100 Mbit/s" ? "MAC_PHY_CTRL_SPEED" : "0u")}} | {{(Value("MAC0", "Duplex") == "全双工" ? "MAC_PHY_CTRL_DUPLEX" : "0u")}};
                {{(negotiate ? "/* Clause 22：仅通告 PHY 支持的 10/100BASE-T 能力。 */\n    if (!(status & 8u)) return;\n    uint16_t advertise = 1u | ((status >> 6) & 0x1e0u);\n    if (!(advertise & 0x1e0u) || !eth_mdio(4,&advertise,true)) return;\n    control = MAC_PHY_CTRL_AUTONEG_EN | (1u << 9);" : "")}}
                if (!eth_mdio(MAC_PHY_REG_CTRL, &control, true)) return;
                eth.Init.AutoNegotiation = MAC_AUTO_NEG_{{(negotiate ? "ENABLE" : "DISABLE")}};
                eth.Init.Speed = MAC_SPEED_{{(Value("MAC0", "Speed") == "100 Mbit/s" ? "100M" : "10M")}};
                eth.Init.DuplexMode = MAC_{{(Value("MAC0", "Duplex") == "全双工" ? "FULL" : "HALF")}}_DUPLEX;
                MAC0->CTRL = eth.Init.MediaInterface | eth.Init.Speed | eth.Init.DuplexMode;
                MAC0->MACMSB = (uint32_t)address[0] << 8 | address[1];
                MAC0->MACLSB = (uint32_t)address[2] << 24 | (uint32_t)address[3] << 16 | (uint32_t)address[4] << 8 | address[5];
                MAC0->TXBASE = (uint32_t)tx;
                MAC0->RXBASE = (uint32_t)rx;
                eth_ready = true;
                {{(negotiate ? "(void)SX_ETH_RefreshLink();" : "")}}
            """);
    }
}
