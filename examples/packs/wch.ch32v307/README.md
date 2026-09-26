# CH32V307 标准库器件包

0.1.3 保留标准库精简 main（`spl`），提供「标准库 + FreeRTOS」（`spl-freertos`）模板，三个收录型号均可选择。RTOS 使用本机 MounRiver 官方 FreeRTOS V10.4.6 移植及专用启动代码，默认两项周期任务更新计数器，不预设板级 GPIO/串口。配置复制到工程的 `include/FreeRTOSConfig.h`，可按需编辑；默认 `heap_4` 与 12 KiB 堆。原始源码及许可保留，来源与 SHA-256 见包内 `vendor/provenance.json`。

支持 CH32V307VCT6、CH32V307RCT6、CH32V307WCU6；不包含其他 CH32 子系列。
资源来自本机 MounRiver Studio 2 的 WCH SDK，标准库版本 3.1。
来源：https://github.com/openwch/ch32v307 。各源文件保留原始版权与仅用于沁恒芯片的许可条件。

- 工具集：`wch.riscv/1.0.0`，沁恒 GCC 12.2.0（发行版 v1.4）；`rv32imac_xw / ilp32`，与官方软浮点 ABI 模板一致。
- 默认外部晶振 8 MHz，系统时钟 144 MHz；配置在 `device/system/system_ch32v30x.c`，`HSE_VALUE` 在包构建定义中。用户修改后应以实际配置为准。
- 启动代码在进入 `main` 前调用官方 `SystemInit`；`System_Init` 完成优先级配置及频率更新。
- 内存布局为 256 KiB Flash / 64 KiB SRAM；板上可配置的 Flash/RAM 划分必须与此一致。本模板不读写选项字节。
- 链接执行地址为 `0x00000000`（Flash 映射），物理 Flash 地址为 `0x08000000`；下载时不能直接套用其他芯片的地址规则。
- `src/main.c` 用于应用；根 CMakeLists 可添加用户文件。`device/` 存放标准库、启动、系统配置和内部 CMake。
- 裸机 `spl` 模板不启用 FreeRTOS；`spl-freertos` 使用官方任务上下文、中断移植与独立 ISR 栈。两个模板均不预设 LED、串口、以太网等引脚，不启用 HAL 或硬件浮点 ABI。
- 0.1.1 增加 WCH-Link / WCH-LinkE 下载（RISC-V / SDI 模式），使用内置沁恒 OpenOCD，支持 400/4000/6000 kHz、单台连接。
- 下载前核对封装对应的芯片 ID、读保护与 256 KiB Flash / 64 KiB SRAM 划分；不匹配时拒绝写入，不自动解锁或修改选项字节。
- 显式启用厂商 `page_erase` 模式，再进行固件写入、校验、复位运行；下载按 Flash 映射地址 0 处理，不能再叠加物理基地址。
- 提供 ELF/BIN/HEX/MAP。IDE 已接入断点、单步、寄存器和调用栈；旧裸机工程实板验收见源码库 `docs/CH32V307-DEBUG-ACCEPTANCE-20260922.md`。三个型号的 FreeRTOS 模板通过真实离线编译；2026-09-26 在 VCT6 / WCH-LinkE 上用独立增强验收固件验证了任务、堆、栈水位及五类注册对象，RCT6/WCU6 尚未实板验收。

维护者使用 `tools/New-Ch32V307Pack.py` 从指定本地 SDK 生成包；脚本校验三个官方模板 ZIP 的 SHA-256。`.mcupack` 不包含 GCC、OpenOCD 或其他工具二进制。
