# AGM AG32 系列器件包来源

按厂商 [2026-06-01 英文参考手册](https://www.agm-micro.com/upload/userfiles/files/AG32%20MCU%20Reference%20Manual%2820260601%E4%BF%AE%E8%AE%A2%E7%89%88%EF%BC%89.pdf) 第 12、317 页的全部订货型号，交付四个独立 StudioX 格式 1 包。器件事实和来源摘要见 `devices.json`。

| 子系列 | 精确型号 | 片内 Flash | 片内 SRAM | 封装 | VE / Supra 目标 |
| --- | --- | --- | --- | --- | --- |
| AG32VF303 | AG32VF303KCU6 | 256 KiB | 128 KiB | QFN32 | AGRV2KQ32 |
| AG32VF303 | AG32VF303CCT6 | 256 KiB | 128 KiB | LQFP48 | AGRV2KL48 |
| AG32VF303 | AG32VF303VCT6 | 256 KiB | 128 KiB | LQFP100 | AGRV2KL100 |
| AG32VF407 | AG32VF407RGT6 | 1 MiB | 128 KiB | LQFP64 | AGRV2KL64 |
| AG32VF407 | AG32VF407VGT6 | 1 MiB | 128 KiB | LQFP100 | AGRV2KL100 |
| AG32VH303 | AG32VH303RCT6 | 256 KiB | 128 KiB | LQFP64 | AGRV2KL64H |
| AG32VH407 | AG32VH407VGT6 | 1 MiB | 128 KiB | LQFP100 | AGRV2KL100H |

七款的手册最大 CPU 频率均为 248 MHz。模板先使用内部时钟；最大频率不是板级默认配置。VH 两款另有 8 MiB PSRAM，不计入片内 SRAM，也不由裸机模板自动启用。VH 逻辑目标和特殊脚以厂商《[AG32VH 系列应用指南](https://www.ag32mcu.com/wp-content/uploads/2025/06/MANUAL_AG32VH_HyperRAM.pdf)》第 3、5、6 页为准；该原厂指南由授权代理托管。普通 VF 封装目标来自原厂 [pinout 表](https://www.agm-micro.com/upload/userfiles/files/AG32_pinout_100_64_48_32_2K.xlsx) 各页表头，并与已锁定 SDK 的转换器核对。

厂商 SDK `builder/common.py` 的未压缩逻辑布局在物理 Flash 末尾保留 100 KiB。256 KiB 器件的应用区是 156 KiB，逻辑地址 `0x80027000`；1 MiB 器件的应用区是 924 KiB，逻辑地址 `0x800E7000`。链接脚本以应用区为边界，包中 Flash 统计仍记录物理总量。压缩或自定义布局需要单独核实，不能沿用本布局。

旧选型表列出的 AG32VF103/107/205、容量变体，以及产品导航中的 VF Pro / ASIC 未在当前订货表中取得完整一致的精确规格，本包不声明其已适配。旧 2024 手册的 AG32VH407RCT6（256 KiB、LQFP64）已不在 2026 手册订货表中，不能据旧开发板网页将它当作当前第八款或自动替换为 VH303 型号。

## 下载与调试探针

器件包声明以下两个选项，均使用内置 AGM 专用 OpenOCD、SWD 和默认 1000 kHz：

| 界面名称 | 包中探针 ID | 内置接口脚本 |
| --- | --- | --- |
| DAP-Link（CMSIS-DAP） | `cmsis-dap` | `interface/cmsis-dap.cfg` |
| J-Link（V9 及以上） | `jlink` | `interface/jlink.cfg` |

[AGM 开发指南](https://www.ag32mcu.com/dev-docs/doc_ag32_vscode_start/) 明确列出 `cmsis-dap-openocd` 和 `jlink-openocd` 的下载、调试流程。本机锁定 SDK 的 `etc/agrv2k.cfg` 默认采用 J-Link / SWD；内置 OpenOCD 的适配器列表和两份接口脚本均已核对，并通过同一 CCT6 目标脚本的离线配置解析。

[SEGGER V9 规格](https://kb.segger.com/J-Link_BASE_V9) 确认支持 SWD。StudioX 通过 AGM OpenOCD 的 SWD 到 RISC-V 调试桥访问 AG32，不启动 SEGGER 原生 J-Link GDB Server。Windows 必须已安装可供 OpenOCD 访问 J-Link 的 USB 驱动；安装方式见 AGM 指南，IDE 不自动替换系统驱动。

旧工程保存的 `agm-blaster` 表示 CMSIS-DAP 兼容选项。当前仅 AG32VF303CCT6 保留已核实的芯片 ID、Flash 容量和逻辑区检查；另外六款的硬件入口仍拒绝未经实板确认的身份。J-Link 的依据为厂商流程及离线脚本验证，本项目尚未用 J-Link 完成实板验收。

## 生成

```powershell
python tools/New-Ag32Packs.py --sdk-directory <framework-agrv_sdk> --platform-directory <AgRV> --freertos-directory <framework-agrv_freertos> --output <新输出目录> --cli <StudioX.Cli.dll>
```

`--stage-only` 只生成包源码和 provenance，不调用 pack CLI。生成器不会下载 SDK、调用 Supra、连接硬件或改写本机 SDK。输出目录必须不存在。编译与映射转换的离线成功不等于硬件下载验收；当前仅 AG32VF303CCT6 保留既有已验收的下载身份检查，其余型号的硬件入口明确拒绝未经验证的身份。

SDK 源码原有版权与许可注释原样保留。本机 `framework-agrv_sdk` 未随附独立 LICENSE 文件，生成器如找到许可证会复制，并如实记录缺失情况，不将平台 manifest 的 Apache-2.0 声明扩大成 SDK 的许可声明。

## FreeRTOS 模板

七个型号均提供一个 `freertos-mcu` 模板，界面名称为 **FreeRTOS**。与其它普通 MCU 模板一致，新建工程时勾选 **启用 AG32 逻辑/Verilog 特殊模式** 才创建 MCU + FPGA 工程；切换模板不改变复选框状态。清单 `ag32Sources` 声明勾选后的配套入口、VE、Verilog 与构建增量，不锁定模式。

- 使用本机 AGM 配套内核 11.1.0、RISC-V 单核移植和 MIT 许可证。厂商网页仍描述旧版 10.4.6，芯片扩展头也保留旧版注释；实际内核版本按 `task.h` 核对，全部输入由 `freertos/source-lock.json` 锁定。
- 默认内部 HSI 10 MHz，1 ms tick、32 KiB heap_4、512 字 ISR 栈、两个 256 字应用任务；支持静态/动态分配、软件定时器、队列及信号量。VH 默认堆仍位于片内 SRAM。
- `main.c` 只保留应用任务及调度入口；时钟、PLIC 初始化、节拍、故障钩子和空闲/定时器任务内存放在 `device/studiox/StudioX_System.c`，统一头文件同时提供外设和 RTOS API。用户配置在 `include/FreeRTOSConfig.h`。
- 任务等待使用 `vTaskDelay`；`Delay_ms/us` 是基于 mcycle 的忙等待，不占用 RTOS 的 MTIME，但不会主动让出 CPU。启动调度后不得直接切换系统时钟或覆盖 CLINT tick。
- 系统层在启动调度器时读取实际系统时钟，包含 PLL 失败退回 HSI 的情况。AGM 浮点扩展派生副本保留 `mepc` 槽位、保存 32 个浮点寄存器和 `fcsr`；外设中断显式转给原厂非嵌套分派表，避免弱符号竞争。差异和原始/派生 SHA-256 记录在 `vendor/provenance.json`。
- 未勾选特殊模式时不预设封装脚，按原理图填写 `pins.ve` 后联合编译映射；勾选后附带 GPIO4_0 → FPGA → GPIO4_1 内部回环，不占用封装脚，已提供可综合顶层，任务变量 `app_logic_value` 可用于后续实板观察。删除演示回环时，在 `include/FreeRTOSConfig.h` 添加 `#undef STUDIOX_AG32_LOGIC_LOOPBACK`，并调整应用任务。

本轮验证为离线工程生成、真实编译及逻辑综合，不代表 RTOS 实板调度验收。器件包内容采用新包版本（VF303 0.1.5，其余 0.1.3），明确合并旧 `freertos-mixed` 条目，旧工程的锁定副本保持原样；IDE 产品版本不变。
