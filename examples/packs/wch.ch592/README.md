# CH592 独立器件包配方

本配方收录本机 MounRiver Studio 2 官方 CH59X/NoneOS SDK 明确提供的 CH592D、CH592F、CH592X。三者分别为 20、28、32 引脚。官方资料未给出经过验证的 D/F/X 只读封装 ID，因此选择器区分具体料号，下载和调试目标脚本仅能只读确认 CH592 家族，不会声称识别了封装。

三个官方模板的链接脚本均限定 `0x00000000` 起 448 KiB 应用代码区、`0x20000000` 起 26 KiB SRAM；本包保留同样的上限，不触及其后的 Data-Flash、Boot 和配置区。精简标准库模板沿用原厂 `CLK_SOURCE_PLL_60MHz`，提供可观察的 `studiox_heartbeat`，不使用开发板特定引脚。请按实板核实时钟源及外设接线。

WCH-Link / WCH-LinkE 的 SDI 目标脚本要求只读芯片 ID `0x92`、外部读取许可位以及调试接口使能位。读取失败、ID 不符或保护启用均拒绝下载与调试；不会自动解锁、更改选项字节或猜测内存布局。

原厂 `StdPeriphDriver/libISP592.a` 保留在包内并参与链接，供用户明确调用 IAP/Data-Flash API。该库和源码均只用于 WCH 制造的器件，原始版权声明保留。包不包含 GCC、CMake、Ninja、OpenOCD，也不预置 BLE 协议栈示例。

来源：本机 MounRiver Studio 2 `WCH/SDK/default/RISC-V/CH59X/NoneOS` 的 CH592D/F/X ZIP、`CH59Xxx.svd` 及目标处理器/下载元数据，制作脚本固定 SHA-256。产品资料入口：<https://www.wch.cn/products/CH592.html>。
