# STM32 系列开发包

这是 StudioX Pack 格式 1。包只包含器件信息、CMSIS、厂商 HAL/SPL、FreeRTOS 10.3.1 和模板，不携带编译器或烧录工具。编译器固定使用 IDE 内置的 arm.gnu 1.0.0。

型号取自 Arm Keil DFP，使用基础料号（如 STM32F103C8，适用于相同芯片的封装/温度后缀）；Flash 与主 SRAM 按型号独立设置。完整目录见 `catalog.json`，来源与输入校验值见 `provenance.json`，第三方许可证见 `licenses/`。

- 四种模板：HAL、SPL、HAL + FreeRTOS、SPL + FreeRTOS。
- 默认板级条件：3.3 V、外部 **8 MHz 无源晶振**，按型号配置最高系统时钟。用户可修改 `include/board.h` 与 `src/board_clock.c`。未配置 LED、串口、USB、外部 RAM 引脚。
- F1：F100 24 MHz、F101 36 MHz、F102 48 MHz、F103/F105/F107 72 MHz。ADC 时钟为 HCLK/6，不超出 14 MHz。
- F4：F401 84 MHz；F410/411/412/413/423 100 MHz；F405/407/415/417 168 MHz；F427/429/437/439/446/469/479 180 MHz（含 Over-drive）。100/180 MHz 模板未承诺 48 MHz USB/SDIO/RNG 时钟，启用这些外设前需要按型号配置独立 PLL 或调整主频。
- 主 SRAM 与 CCM 分开链接。CCM 可选 `.ccm_noinit` 段不自动初始化，不可作为 DMA 缓冲区。模板不使用备份 SRAM/外部存储器。
- FreeRTOS 堆按 RAM 容量生成；小于等于 10 KiB RAM 的型号禁用软件定时器，使用较小的初始任务栈；增加任务后应调整堆和栈并检查运行时剩余量。
- HAL + FreeRTOS 用 F1 的 TIM2 / F4 的 TIM5 做 HAL 时基（占用该定时器），SysTick 留给 FreeRTOS。
- 用户业务源码放 `src/`、头文件放 `include/`，修改根 CMakeLists.txt；`device/` 下为固定的器件支持。
- ST-Link、CMSIS-DAP、J-Link 默认 SWD 2 MHz。下载前核对 DBGMCU 器件组 ID 和 Flash 容量，不修改选项字节，不做整片擦除。ID 无法区分同一硅片组的所有引脚/功能变体，用户仍需选择实际型号。

维护者使用 `tools/New-Stm32SeriesPack.py` 构建包；这不是最终用户运行依赖。编译检查和 OpenOCD 离线配置检查不等于实板验证。
