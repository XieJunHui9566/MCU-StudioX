# Zephyr 探索者 V3.5 实板验收记录（实验模式，2026-09-28）

## 范围与结果

用户确认实物为正点原子探索者 V3.5，并明确授权将 SHA-256 为 `0ADFA1B5B89BB3DFFCB8D865B4BA89C95864539444ADBDB00E75EDC966FA6C79` 的 Zephyr v4.4.2 `blinky` BIN 烧录到当前连接的板上。目标探针为 ST-Link，序列号 `37FF71064E57343640571643`；STM32CubeProgrammer 识别器件 ID `0x413`、1 MiB Flash、Cortex-M4，属于 STM32F405/F407/F415/F417 家族。此识别本身不能证明 PCB 修订或芯片完整料号。用户选择验收结束后保留 `blinky` 在板上运行。

烧录固件来自 v0.1.0 实验包生成工程。当前 v0.1.1 实验包位于 [zephyr.alientek-explorer-v35-0.1.1.mcupack](../artifacts/packs/Zephyr-Experimental-0.1.1/zephyr.alientek-explorer-v35-0.1.1.mcupack)，SHA-256 为 `4DA2532DAB00575E98686DCB36C98B2A70A8DCCED69A734110E42E54D1BC34D8`。v0.1.1 调整 OpenOCD runner 和板级说明，最后一次重包还修订说明与源码注释。最终归档已重新导入并完整编译，其 BIN 与板上固件同 SHA-256；`board.cmake` 和 `openocd.cfg` 与实测时逐字节相同。包内不携带已烧录固件。

**结论：这份实验固件在该实板上的烧录、读回、PF9 GPIO 翻转及 OpenOCD/GDB 硬件断点和单步已验证。**用户目视确认 LED0 持续闪烁；这是用户观察，不是本代理的光学测量。IDE 内 Zephyr 构建、下载、调试入口未因此完成验收。

## 原始证据

所有本次硬件日志和 Flash 镜像均保存在 `artifacts/validation/zephyr-explorer-v35-20260928/`：

| 项目 | 结果与证据 |
| --- | --- |
| 烧录前保护配置 | [烧录前选项字节](../artifacts/validation/zephyr-explorer-v35-20260928/preflight-option-bytes.log)：RDP `0xAA`（Level 0），BOR_LEV `3`，WDG_SW、nRST_STOP、nRST_STDBY 均为 `1`，WRP0–11 均为 `1`。 |
| 原 Flash 备份 | [首次备份日志](../artifacts/validation/zephyr-explorer-v35-20260928/backup.log)、[复核日志](../artifacts/validation/zephyr-explorer-v35-20260928/backup-check.log)及对应的 [flash-before.bin](../artifacts/validation/zephyr-explorer-v35-20260928/flash-before.bin)、[flash-before-check.bin](../artifacts/validation/zephyr-explorer-v35-20260928/flash-before-check.bin)：各 1,048,576 字节，逐字节相同，SHA-256 均为 `B8681E49320CF7FD116803E48DD403C1C3579E63ECDB47D3E7CC07B46A14BEB8`。 |
| 烧录与读回 | [烧录日志](../artifacts/validation/zephyr-explorer-v35-20260928/flash-and-verify.log)报告擦除扇区 0、1，写入 `0x08000000`，校验成功。[读回日志](../artifacts/validation/zephyr-explorer-v35-20260928/readback.log)与 [flash-after.bin](../artifacts/validation/zephyr-explorer-v35-20260928/flash-after.bin)记录完整 1 MiB 回读。读回前 16,900 字节与授权 BIN 逐字节相同；从偏移 `0x8000` 起的扇区 2 及以后与烧录前备份逐字节相同。扇区 0、1 内固件末尾至 `0x8000` 为擦除态 `0xFF`。 |
| GPIO 运行 | [暂停、读取、恢复原始日志](../artifacts/validation/zephyr-explorer-v35-20260928/pf9-halt-samples.log)及 [采样表](../artifacts/validation/zephyr-explorer-v35-20260928/pf9-halt-samples.csv)：GPIOF ODR `0x40021414` 的 PF9 位在 12 次采样中出现 `0` 和 `1`，每次读取前暂停、读取后恢复。用户另确认 LED0 持续闪烁。 |
| 硬件调试 | [OpenOCD 附加日志](../artifacts/validation/zephyr-explorer-v35-20260928/openocd-attach-after-dbg-sleep.log)识别 Cortex-M4 与硬件断点资源；[GDB 日志](../artifacts/validation/zephyr-explorer-v35-20260928/gdb-main-break-step.log)显示在 `main` 地址 `0x080003D4` 命中硬件断点，单步后 PC 到 `0x080003D6`。GDB 把源码行映射到生成的 `device.h`，且报告 CP1252 编码警告，因此这次不算 IDE 用户源码行级断点验收。 |
| Zephyr runner 路径 | SHA-256 为 `4DA2532DAB00575E98686DCB36C98B2A70A8DCCED69A734110E42E54D1BC34D8` 的最终 v0.1.1 包经 C# 服务导入并生成工程，[构建日志](../.artifacts/zephyr-workspace/build-studiox-v011-current.log)完成 `155/155`，产出 16,900 字节 BIN，与已烧录 BIN 同 SHA-256。生成的 [runners.yaml](../.artifacts/zephyr-workspace/build-studiox-v011-current/zephyr/runners.yaml)记录 `--no-halt`、`monitor reset halt` 和 `maintenance flush register-cache`。另一次使用相同 `board.cmake`、`openocd.cfg` 和同 SHA-256 BIN 的 v0.1.1 工程，其 [west attach 实板日志](../artifacts/validation/zephyr-explorer-v35-20260928/west-attach-final-pack.log)在 `main` 命中硬件断点、单步到 `main+2` 后退出。日志中有无关 runner `rtsflash` 缺少 Python `usb` 模块的警告，不影响此次 OpenOCD 附加结果。 |
| 烧录后保护配置 | [复位保持状态下的重读](../artifacts/validation/zephyr-explorer-v35-20260928/postflight-option-bytes-under-reset.log)与烧录前相同：RDP `0xAA`，BOR_LEV `3`，上述用户配置和 WRP0–11 均一致；日志末尾 `Core run`。本次未写选项字节。 |

首次运行态 SWD 附加的 [OpenOCD 失败日志](../artifacts/validation/zephyr-explorer-v35-20260928/openocd-live-attach.log)记录目标检查失败。复位保持状态下读取 [DBGMCU_CR](../artifacts/validation/zephyr-explorer-v35-20260928/dbgcr-before.log) 为 `0`，即 `DBG_SLEEP` 位为 `0`；在该状态下写入易失性寄存器 `0xE0042004` 的 bit 0 后继续运行，见[写入与运行日志](../artifacts/validation/zephyr-explorer-v35-20260928/dbg-sleep-enable-run.log)，随后 [Hot Plug 读回](../artifacts/validation/zephyr-explorer-v35-20260928/hotplug-after-dbg-sleep.log)可读到 bit 0 为 `1`，OpenOCD 附加成功。OpenOCD 后续记录寄存器值为 `0x00000007`，属于运行时调试寄存器状态，不是选项字节或 Flash 改写。

v0.1.1 runner 在 Zephyr SDK 1.0.1 中使用 `stlink-dap.cfg`、`dapdirect_swd` 和 `connect_assert_srst`，经 NRST 保持复位附加。`west attach` 的 GDB 初始化再执行 `monitor reset halt` 与寄存器缓存刷新；**附加会复位并暂停板子**，但不烧录。此处验证的是外部 West/OpenOCD/GDB 路径，IDE 内 Zephyr 调试入口仍禁用。

[运行态 Hot Plug 选项字节日志](../artifacts/validation/zephyr-explorer-v35-20260928/postflight-option-bytes.log)曾返回与前后复位保持读取矛盾的 RDP/WRP 值；运行态 AP 读取同时出现不可信寄存器值，因此**不采用该日志判定选项字节**。以烧录前及烧录后复位保持状态下的两份读取为准。

## 边界

- 用户提供的文件名为 `EXPLORER_V3.5.pdf`，图纸标题栏却写 V3.4；本次运行只支持 PF9/LED0 连接在所测实板上可用，不能由此确认整套 V3.5 引脚表。
- 未测 USART1、CH340C/P10 跳线、其它 LED/按键及长期运行稳定性；LED 闪烁周期没有仪器测量。
- 该记录仅针对当前 ST-Link 与这块板、这份 BIN。原 Flash 备份保留在上述证据目录，板上当前保留 Zephyr `blinky`。
