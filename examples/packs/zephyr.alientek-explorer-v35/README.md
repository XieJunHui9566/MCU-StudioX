# 正点原子探索者 V3.5：Zephyr 实验包

此包使用独立的 `studiox.zephyr-pack` 格式，扩展名仍为 `.mcupack`。它适用于 Zephyr **v4.4.2**，板目标是 `alientek_explorer_f407zg`。包内只有工程模板和板定义，不含 Zephyr SDK、编译器或固件。

**实验模式：仅 LED0/PF9 已经 V3.5 实板验证，其它板级引脚仍待核对。** 用户确认实物为探索者 V3.5；提供的 `EXPLORER_V3.5.pdf` 六页标题栏均写 `Revision: V3.4`。下列连接从这份原理图读取，不能把图纸文件名当成板级版本的独立证据。

| 资源 | 图纸连接 | 板定义 |
| --- | --- | --- |
| MCU | STM32F407ZGT6 | Zephyr `stm32f407xx` SoC，1 MiB Flash、128 KiB SRAM、64 KiB CCM |
| HSE | 8 MHz 晶振 | PLL 168 MHz；APB1 42 MHz、APB2 84 MHz |
| 调试 | PA13 SWDIO、PA14 SWCLK、NRST 引到 JTAG 插座 | 外接 ST-Link / OpenOCD；复位保持附加已在所测板通过，runner 需要 NRST |
| USB 串口 | CH340C 的 TXD/RXD 经 P10 两组跳线至 USART1 的 PA10/PA9 | 控制台 USART1，115200；跳线实际安装状态待核实 |
| LED | LED0 PF9、LED1 PF10，接至 3.3 V | 两个 LED 均低电平点亮；已验证 LED0/PF9，LED1 未验证 |
| 按键 | KEY0 PE4、KEY1 PE3、KEY2 PE2 接地；WK_UP PA0 接 3.3 V | 前三者低有效，WK_UP 高有效 |

板文件沿用 Zephyr v4.4.2 已支持的 `STM32F407Xg` SoC 与 STM32 HAL pinctrl，DTS 和 Kconfig 等板级文件改写自上游 `black_f407zg_pro` 板定义，保留原作者 Intel Corporation 的署名和 Apache-2.0 声明。完整许可证见包内 `LICENSE`，生成工程也会携带一份。包内不复制上游 SDK。v0.1.1 的 OpenOCD 配置使用 Zephyr SDK 1.0.1 的 `stlink-dap.cfg`、`dapdirect_swd` 及经 NRST 的 `connect_assert_srst`；`west attach` 连接 GDB 后执行 `monitor reset halt` 与 `maintenance flush register-cache`。**附加会复位、暂停目标**，但不会因此自动写入 Flash。IDE 应显式区分附加调试与下载固件，不能因为项目存在 `board.cmake` 就开始烧录。

构建示例：在已安装并初始化 Zephyr v4.4.2 的工作区，使用 `west build -b alientek_explorer_f407zg <生成的工程目录>`。该命令仍需本机 Zephyr 依赖和工具链。v0.1.1 包的导入、工程生成、外部编译和 `west attach` 实板断点/单步曾通过验证；归档校验值与原始证据记录在 MCU StudioX 仓库的 `docs/ZEPHYR_HARDWARE_ACCEPTANCE_20260928.md`，不随本包分发。用户授权烧录的 BIN 来自 v0.1.0，SHA-256 为 `0ADFA1B5B89BB3DFFCB8D865B4BA89C95864539444ADBDB00E75EDC966FA6C79`；v0.1.1 生成工程的 BIN 与其逐字节相同。IDE 内 Zephyr 构建、下载、调试入口仍禁用。

来源：`EXPLORER_V3.5.pdf`（仅作为本地参考，不在包内分发）、[Zephyr v4.4.2 板级移植指南](https://github.com/zephyrproject-rtos/zephyr/blob/v4.4.2/doc/hardware/porting/board_porting.rst)及[同 SoC 的上游板定义](https://github.com/zephyrproject-rtos/zephyr/tree/v4.4.2/boards/others/black_f407zg_pro)。
