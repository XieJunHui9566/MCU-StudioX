# STM32 F1 / F4 子系列包

2026-09-21：按子系列分别发行 23 个包，共 244 个基础型号；不是 F1/F4 混合包。

| 范围 | 子系列 | 基础型号数 |
|---|---|---:|
| STM32F1 | F100、F101、F102、F103、F105、F107 | 95 |
| STM32F4 | F401、F405、F407、F410、F411、F412、F413、F415、F417、F423、F427、F429、F437、F439、F446、F469、F479 | 149 |

初始 0.1.0 输出曾位于 `artifacts/packs/STM32/`，已在 开发产物清理（本地记录） 中删除；当前包与清单位于 `artifacts/packs/STM32-0.1.1/`。包 ID 为 `studiox.stm32f103` 等。同子系列不同容量/封装共用一个包，型号来自 Keil DFP；使用基础料号，例如 F103C8T6 对应 STM32F103C8。新建工程支持型号搜索，必须从厂商目录中明确选择型号。

每个型号提供 HAL / SPL / HAL + FreeRTOS / SPL + FreeRTOS 四种模板。SDK 在创建工程时按模板隔离；根 CMakeLists.txt 管理用户源码，device/CMakeLists.txt 管理固定 SDK。已生成的旧 F407ZG 工程不被批量改写，新系列包与旧测试包使用不同 ID。

## 0.1.1 精简应用入口

最新输出位于 `artifacts/packs/STM32-0.1.1/`，仍按 23 个子系列分别打包。新增 `include/system_config.h`；`src/` 默认仅有 `main.c`。晶振/PLL、HAL 初始化、系统时基、中断、RTOS 启动和错误钩子集中在 `device/system/`，由内部 CMake 编译，工程树默认折叠该目录。

裸机入口只调用 `System_Init()` 并提供应用循环；RTOS 入口保留用户创建任务的位置，再调用 `System_Start()`。初始 RTOS 工程运行内核空闲/定时器任务，不再生成心跳演示任务。用户在调度器启动前创建自己的任务，任务函数可放进自己新增的源文件；任务中可以使用 `System_Delay()`。没有通过重命名/重入 main 等隐式方式启动应用。

库版本、芯片参数、外部 8 MHz 晶振和默认最高时钟沿用 0.1.0。当前包从下文列出的 SDK 源库重新构建；`tools/New-Stm32Packs.ps1` 生成 0.1.1 子系列包并建立索引。本工作区已不保留旧 0.1.0 归档。

## 时钟、内存和下载

默认 3.3 V、外部 8 MHz 无源晶振。F100/F101/F102/F103 对应 24/36/48/72 MHz；F105/F107 为 72 MHz。F401 为 84 MHz；F410/411/412/413/423 为 100 MHz；F405/407/415/417 为 168 MHz；其余本次收录 F4 为 180 MHz，包含 Over-drive。

每个型号独立配置 Flash、主 SRAM、CCM、启动文件、HAL 宏、SPL 密度宏和 OpenOCD 身份检查。CCM 不计入主 SRAM。F1 XL 型号声明第二个 Flash bank（0x08080000），避免大于 512 KiB 固件下载不完整。烧录器提供 ST-Link、CMSIS-DAP、J-Link，默认 SWD 2 MHz；下载前验证器件组 ID 与容量，不改选项字节。

HAL + FreeRTOS 使用 F1 TIM2 / F4 TIM5 作为 HAL 时基，SysTick 由 FreeRTOS 管理。4–10 KiB RAM 的型号自动缩小任务栈、堆并关闭软件定时器；应用扩展时仍需评估实际栈使用。默认不初始化 USB、SDIO、LED、串口、外部 RAM。100/180 MHz F4 使用 USB/SDIO/RNG 等外设时，需按型号另外配置外设时钟，不把主 PLL 的 Q 输出错误地当成 48 MHz。

## SDK 来源

- F1 HAL 1.1.10 / CMSIS：本机 ST STM32CubeF1 1.8.7。
- F4 HAL 1.8.5 / CMSIS / FreeRTOS 10.3.1：本机 ST STM32CubeF4 1.28.3。
- F1 SPL：从 [Arm Keil 官方 STM32F1xx_DFP 2.4.1](https://www.keil.arm.com/packs/stm32f1xx_dfp-keil/versions/) 下载；该包更新说明标记 SPL 3.6.0，其中 RCC 源文件版本为 3.6.2，保留原始版本说明。
- F4 SPL 1.8.0：ST 直接下载连接失败后，使用 [µOS++ 的 STM32 Vendor Archives](https://sourceforge.net/projects/micro-os-plus/files/Vendor%20Archives/STM32/) 发行归档。它是由 ilg-ul 维护的第三方厂商归档，不能声称 SHA 是 ST 官方发布值。原产品为 [STSW-STM32065](https://www.st.com/en/embedded-software/stsw-stm32065.html)。
- F4 型号信息：Keil STM32F4xx_DFP 3.1.1 PDSC；F1 型号信息随上述 2.4.1 包取得。

下载原包 SHA-256、来源 URL 及实际收入的 SDK 文件 SHA-256 都保存在包内 provenance.json；许可证完整保留在 licenses/。SDK 不包含编译器或 IDE 插件，导入时不联网。

## 重建

维护者需要 Python 3（仅标准库）与 .NET；普通用户不需要 Python。先运行 `tools/Build.ps1` 构建打包 CLI，再显式运行：

```powershell
./tools/Get-Stm32Sources.ps1 -Destination '<SDK_ROOT>/stm32'
./tools/New-Stm32Packs.ps1 -CubeF1Directory '<SDK_ROOT>/STM32Cube_FW_F1_V1.8.7' `
  -CubeF4Directory '<SDK_ROOT>/STM32Cube_FW_F4_V1.28.3' `
  -SourceDirectory '<SDK_ROOT>/stm32' -OutputDirectory '<PACK_OUTPUT_DIR>'
```

输出文件必须尚不存在；不能以相同包 ID/版本覆盖已发布的不同内容。下载器先校验固定 SHA；厂商 PDSC 更新导致 SHA 变化时拒绝接受，需维护者审核版本后更新配方。`-AggregateForValidation` 仅用于内部矩阵检查，混合测试包不得放入发行目录。

## 验证记录

0.1.1 布局更新后，F100C4（4 KiB RAM）、F103C8、F407ZG 各四模板重新编译成功，共 12 组；同时检查生成产物、内存/向量与三烧录器离线配置。24 个新归档的 SDK 哈希索引均与对应 0.1.0 相同，980 个模板项检查通过。结果位于 `.artifacts/system-layout-f100`、`system-layout-f103`、`system-layout-f407`；此处的编译覆盖为代表型号，不表示每个模板项均已编译或实板测试。

实际调用 IDE 的工程生成器和内置 Arm GNU / CMake / Ninja，完成 **F1 60 组、F4 92 组** 代表性编译（按 CMSIS 启动/型号宏与 SPL 密度宏组合选择最小 Flash/RAM 型号）。全部成功且无编译警告，检查 ELF/BIN/HEX/MAP、RAM/Flash 上限、初始栈与复位向量、FreeRTOS 三个异常向量和 SDK 隔离。

拆包后对 F103C8/F103RF、F410C8、F446MC 再检查四模板，其中 F1 XL 双 bank 与 F4 PLLR 的配置修正有对应覆盖。三种 OpenOCD 接口均离线解析，另以 Tcl 模拟读值验证身份检查能拒绝错误 ID/容量。F103 和 F446 工程进行实际 clangd 库函数/寄存器/FreeRTOS 提示、声明跳转及只读标准头导航检查；WPF 预览检查独立包导入、型号搜索、四模板、编辑界面与三烧录器选择。

可复用检查入口：`tools/StudioX.Stm32Validation/`。矩阵原始结果在 `.artifacts/stm32-matrix-f1-v2/results.json`、`.artifacts/stm32-matrix-f4-v2/results.json`；拆包检查在 `.artifacts/stm32-final-*`；界面预览在 `.artifacts/stm32-subseries-ui-v2/`。这不是对 244 个型号逐一进行实板测试，本次没有连接或烧写 STM32 硬件。
