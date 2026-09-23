# STM32F407ZGT6 器件包

StudioX Pack 格式 1，ID `studiox.stm32f407zg`，版本 `0.1.0`。工具集为 IDE 内置 `arm.gnu/1.0.0`，编译器 `arm-gnu-15.2.rel1`，包中不包含工具程序。不枚举未经适配的其他 STM32F4 型号。

## 四种工程

| 模板 | 库 | 时基 |
|---|---|---|
| hal | HAL 1.8.5 | SysTick / HAL |
| spl | 标准外设库 1.3.0 | SysTick / 裸机毫秒计数 |
| hal-freertos | HAL + FreeRTOS 10.3.1 | SysTick / RTOS，TIM6 / HAL |
| spl-freertos | 标准外设库 + FreeRTOS 10.3.1 | SysTick / RTOS |

RTOS 使用 GCC ARM_CM4F 移植、heap_4（32 KiB）、1 kHz tick、500 ms 心跳任务。HAL+RTOS 默认占用 TIM6，应用使用其它定时器。FreeRTOS 使用 4 位抢占优先级，调用 FromISR 的中断优先级数值必须不小于 5。

四种模板都以外部 **8 MHz 无源晶振**为 HSE，PLL M=8 / N=336 / P=2 / Q=7，SYSCLK/HCLK=168 MHz，APB1=42 MHz，APB2=84 MHz，PLL48=48 MHz。按常见 3.3 V 开发板设置电压 Scale 1 和 Flash 5 等待周期。晶振启动失败会停在 `StudioX_Panic`，通过 `app_error_code` 定位，不静默降频。默认只递增 `app_heartbeat`，不假设开发板 LED 或串口引脚。

Flash 1 MiB，主 SRAM 128 KiB；额外 64 KiB CCM 通过 `.ccm_noinit` 显式使用，该区不能给 DMA 使用，也不由启动代码自动清零。

## 创建与编辑

导入 `.mcupack`，选择 STM32F407ZGT6，再选上述模板。根 `CMakeLists.txt` 管理用户代码；固定 SDK/寄存器定义/启动文件放在 `device/`，只复制所选库与 RTOS 的 SDK 资源。模板宏与包含目录也同步给代码提示。

`src/main.c`、`src/board_clock.c`、`src/stm32f4xx_it.c` 和 `include/` 属于用户代码。HAL 配置文件默认启用常用模块，可按需启用其他模块；相应库源码已经列入内部 CMake。SPL 使用独立头文件，支持常见 F407 外设。RTOS 工程另外提供 `FreeRTOSConfig.h` 和失败处理钩子。更换晶振时，应同时调整 HSE_VALUE 以及板级时钟配置。

## 下载

工具栏下载按钮或「构建 → 下载固件…」选择 ST-Link、CMSIS-DAP 或 J-Link，默认 SWD 2 MHz，可指定速度与序列号。先保存、编译，再写入 BIN 映像范围、校验并复位运行。USB 驱动需能被内置 OpenOCD 访问；三种协议配置不表示所有烧录器固件版本均已实测。

目标配置核对 STM32F405/407 的硅片系列 ID `0x413` 与 1024 KiB Flash；这些寄存器不能鉴别具体封装，型号仍需按实际开发板选择。不会自动全片擦除、解除读保护、修改选项字节或 OTP。下载偏好存于 `.studiox/download.json`，实际固件快照和原始日志存于 `.build/download-*/`。

## 来源和重建

从本机 STM32CubeF4 1.28.3 提取 CMSIS、HAL 1.8.5 和 FreeRTOS 10.3.1；SPL 1.3.0 来自 Keil STM32F4xx_DFP 1.0.8。保留厂商源码中的许可和包内 `licenses/`。HAL 配置及 TIM6 时基由厂商模板生成，其余板级模板由 StudioX 提供。没有读取旧 IDE 的包清单或插件代码。

```powershell
.\tools\New-Stm32F407Pack.ps1 -CubeF4Directory '<STM32Cube_FW_F4_V1.28.3>' -SplDeviceDirectory '<STM32F4xx_DFP 1.0.8 的 Device 目录>'
```

规格依据：[ST STM32F407ZG](https://www.st.com/en/microcontrollers-microprocessors/stm32f407zg.html)；RTOS 优先级规则：[FreeRTOS Cortex-M](https://freertos.org/Documentation/02-Kernel/03-Supported-devices/04-Demos/ARM-Cortex/RTOS-Cortex-M3-M4)。
