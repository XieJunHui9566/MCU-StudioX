# STM32F407ZGT6 适配验证 / 2026-09-21

## 已完成

- 新 `.mcupack` 使用 StudioX 原生清单，不依赖旧 IDE 的包格式、工程或插件。
- 模板构建参数、用户文件映射和 OpenOCD 目标/接口配置加入包模型；模板选择同时影响实际编译和 clangd。
- 根据所选模板复制 SDK，HAL 与 SPL 不混用；裸机工程不复制 RTOS 内核。
- 四种工程均用内置 Arm GNU GCC 15.2.1 / CMake 4.4.0 / Ninja 1.10.2 实际构建成功，编译输出无警告。

| 模板 | BIN 字节 | 构建 / 启动向量 | 提示与声明跳转 |
|---|---:|---|---|
| HAL | 4,020 | 通过 | HAL、寄存器、内置标准头文件通过 |
| HAL + FreeRTOS | 11,648 | 通过 | HAL、寄存器、RTOS、内置标准头文件通过 |
| SPL | 1,684 | 通过 | SPL、寄存器、内置标准头文件通过 |
| SPL + FreeRTOS | 8,260 | 通过 | SPL、寄存器、RTOS、内置标准头文件通过 |

所有映像的初始 MSP 为 `0x20020000`，复位向量为 Flash 映像内有效 Thumb 入口。ELF/BIN/HEX/MAP 均存在。

OpenOCD 的 ST-Link、CMSIS-DAP、J-Link 接口与 STM32F407 目标配置均通过离线解析，显式在 `init` 之前退出，没有接触硬件。参数检查覆盖未知烧录器、非法序列号及 Tcl 特殊字符；包校验拒绝模板覆盖 `.studiox/project.json`。下载脚本先调用目标身份检查，再按固件范围写入并校验，不含整片擦除或选项字节操作。

隔离桌面检查覆盖导包、四种模板选择、HAL+FreeRTOS 工程创建、下载按钮启用，以及深浅主题的烧录器/速度/序列号对话框。源码构建 0 警告、0 错误。

本机记录：`.artifacts/stm32-final-check/result.txt`、四份构建日志和三份接口解析日志；`.artifacts/stm32-ui-final/` 保存界面检查结果。

## 实板范围

当前只按器件规格配置 HSE 8 MHz、PLL M/N/P/Q = 8/336/2/7、SYSCLK 168 MHz、APB1 42 MHz、APB2 84 MHz，未在 STM32 开发板上测量实际时钟或运行四种固件。ST-Link/DAP/J-Link 的 USB 驱动兼容性、下载/复位和 RTOS 调度仍需相应实物验证。此前 AG32 的烧录结果不算作 STM32 验证。
