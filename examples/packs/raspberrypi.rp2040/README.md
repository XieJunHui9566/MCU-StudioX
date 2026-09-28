# RP2040 C SDK 器件包

独立 StudioX 格式 1 包 `raspberrypi.rp2040/0.1.0`，使用官方 Pico SDK 2.2.0。
板级目标 `RP2040-PICO` 为 Raspberry Pi Pico / Pico H：双核 Cortex-M0+、外部 12 MHz 晶振、SDK 默认 125 MHz 系统时钟、2 MiB QSPI Flash、264 KiB SRAM（256 KiB 主区和两个 4 KiB scratch 区）。Flash 是板载外部器件，不能将此配置套用到任意 RP2040 板。

提供最小 C 工程、GPIO 闪灯和双核队列三个模板，入口均为 `src/main.c`；闪灯使用 Pico 的 GP25。双核示例使用 SDK 队列同步，并以 `response_timeouts` 记录响应超时。没有 MicroPython、无线或 RTOS 模板。

用户工程使用内置 `arm.gnu/1.0.0` 的 GCC/CMake/Ninja，生成 ELF/BIN/HEX/MAP；不需要本机安装 Pico SDK、Python 或 picotool。包作者使用官方 CMake 和编译器依赖提取 SDK 源码、启动文件、配置头及预生成 boot2 校验汇编，保留来源、逐文件 SHA-256 和许可证。

包含 pico_stdlib、multicore、queue/sync、rand/unique_id，以及 GPIO、UART、定时器、时钟、ADC、DMA、Flash、I2C、SPI、PIO、PWM、interp、RTC 等 C API 依赖。根 CMake 管理用户源码，不调用 `pico_sdk_init()`；这是已解析的 SDK 源码包。USB/TinyUSB、Pico W、PIO 汇编生成、UF2 生成及任意上游 CMake 工程导入不在本包范围。

本包用于工程创建、编辑和离线编译；未提供经过实板核验的下载/调试配置，IDE 不开放这两个入口。RP2350 的硬件验收不能用于 RP2040。

维护说明见仓库 `docs/RP2040.md`。

来源：https://github.com/raspberrypi/pico-sdk/tree/2.2.0 。
官方板卡说明：https://www.raspberrypi.com/documentation/microcontrollers/pico-series.html 。
