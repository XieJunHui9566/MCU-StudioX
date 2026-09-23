# RP2350 C SDK 器件包

`raspberrypi.rp2350/0.1.0`，Pico SDK 2.2.0，面向 Pico 2 / 立创同配置兼容板：RP2350A、外部 12 MHz 晶振、150 MHz Cortex-M33、4 MiB QSPI Flash、520 KiB SRAM。

三个模板均为 C：最小工程、GPIO 闪灯、双核队列示例。闪灯模板默认 GP25，兼容板须核对 LED 引脚。没有 MicroPython 固件、解释器、Python 用户工程或 Python 烧录流程。

保留 SDK 源文件及许可证。包制作时使用官方 CMake 导出精确依赖、生成配置头文件及 boot2 校验数据；用户构建直接使用 IDE 内置 `arm.gnu/1.0.0`，不依赖本机 Pico SDK、Python、picotool 或网络。固定板型配置在 `device/sdk/generated/pico_base/pico/config_autogen.h` 与 `boards/pico2.h`，链接布局在 `device/linker/rp2350-pico2.ld`。

收录 pico_stdlib、multicore、queue/sync、rand/unique_id，以及 GPIO、UART、定时器、时钟、ADC、DMA、Flash、I2C、SPI、PIO、PWM、interp、powman、SHA256 等 C API 的所需依赖。它是本板型的已解析 SDK 源码集，不是完整 SDK CMake 工程导入器；新增独立 SDK 子库、USB/TinyUSB、无线、RTOS、PIO 汇编生成或签名工具需单独接入。未使用的函数由链接器删除。

默认生成 ELF/BIN/HEX/MAP；下载使用 DAP-Link (CMSIS-DAP)，1000 kHz，支持设置探针序列号。目标配置在加载 rp2350.cfg 前启用 `SWD_MULTIDROP`，并在写入前检查芯片 ID 和 Flash 容量。调试只附加并校验当前固件，不自动烧录或修改 OTP。

此包选择 ARM Secure Cortex-M33，不包含 Hazard3/RISC-V 或 Pico 2 W 板型。两个核心由 OpenOCD 联合暂停；IDE 当前源码窗口跟随所选 GDB 线程，尚无专用核心选择界面。

重建及验证见仓库 `docs/RP2350.md`。上游：https://github.com/raspberrypi/pico-sdk/tree/2.2.0 。
