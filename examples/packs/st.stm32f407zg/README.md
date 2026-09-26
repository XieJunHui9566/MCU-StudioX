# STM32F407ZG 共享模板素材

本目录保留现行器件包配方仍需读取的共享素材：

- `templates/common/main.c`、`interrupts.c`、`rtos_hooks.c` 和 `FreeRTOSConfig.h`：STM32 F1/F4 子系列配方的模板输入。
- `linker/stm32f407zg.ld`：STM32 子系列配方按准确型号调整内存容量的链接脚本输入。
- `support/runtime.c`：STM32、普冉和兆易创新等配方复用的运行时支持源码。

旧单型号专用的 `board.h`、`board_clock.c` 和调试配置已删除。现行配方从 `st.stm32-series/board_clock.c` 读取时钟模板，并按型号生成 `board.h` 与精确目标调试配置；其余共享素材继续保留。

STM32 器件包现在按子系列生成。维护者使用 [`tools/New-Stm32Packs.ps1`](../../../tools/New-Stm32Packs.ps1)；SDK 来源、构建命令、包清单和验证范围见 [`docs/STM32_SERIES.md`](../../../docs/STM32_SERIES.md)。
