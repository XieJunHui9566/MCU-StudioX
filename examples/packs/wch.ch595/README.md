# CH595 独立器件包配方

本配方只收录本机 MounRiver Studio 2 官方 NoneOS SDK 明确提供的 CH595D、CH595F、CH595X。D/F/X 分别为 20/28/32 引脚；不据此推断开发板的 LED、UART 或其他外设接线。原始 SDK 文件完整保留版权声明。

官方工程说明列出 256 KiB ROM 与 32 KiB SRAM，但链接脚本、`CH59x_flash.h` 和 WCH ISP 的用户可编程代码区上限均为 240 KiB。包保留 256 KiB 物理 Flash 容量，并把链接和下载约束为 `0x00000000` 至 `0x0003BFFF`。余下 16 KiB 不作应用固件写入。

精简标准库模板使用内部 HSI + PLL 80 MHz，提供可观察的 `studiox_heartbeat`，不使用任何板级引脚。CH595 属 WCH RISC-V 平台；工具集采用独立的 `wch.riscv/1.0.0`，编译器、CMake、Ninja、WCH OpenOCD 不封入 `.mcupack`。

WCH-Link SDI 目标脚本只读核对 CH595 家族 ID `0x97` 和读保护状态。当前官方头文件未提供经核实的 D/F/X 封装只读判别值，因此目标脚本不会宣称可区分三个封装。发生 ID 不符、保护开启或读取失败时，拒绝继续下载与调试；不会自动解锁或修改选项字节。

来源：本机 MounRiver Studio 2 `WCH/SDK/default/RISC-V/CH595/NoneOS` 的 D/F/X ZIP、`CH595x.svd`、目标处理器和下载元数据。制作脚本固定其 SHA-256，输出 `vendor/provenance.json`。上游资料入口：<https://www.wch.cn/products/CH595.html>。
