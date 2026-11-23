# MCU StudioX

MCU StudioX 是面向多系列微控制器的 Windows x64 嵌入式 IDE。它把工程创建、代码编辑、编译、烧录、调试和串口观察放在同一工作台。当前源码版本 **0.2.6.15**。

## 下载与开始使用

- 下载 [最新版 0.2.6.15 预发布安装包](https://github.com/XieJunHui9566/MCU-StudioX/releases/tag/v0.2.6.15-update-20261103)，也可查看[所有发行](https://github.com/XieJunHui9566/MCU-StudioX/releases)。本页的功能介绍以 `main` 分支为准，安装包的功能和预装组件见各自的发行说明。
- **轻量版**是单个 `Light-Setup.exe`，开发组件可按工程需要准备。**完整版**预装该发行列出的工具，下载同版的安装 EXE 和全部配套 `.bin` 分卷，放在同一目录后运行 EXE。[安装与升级](docs/INSTALLER.md)
- 打开 IDE 后选择 **新建工程**，搜索具体芯片并选择模板。已有 STM32CubeMX CMake 工程可以通过 **导入 CubeMX CMake 工程** 打开。[第一个工程](docs/FIRST_PROJECT_GUIDE.md) · [CubeMX 导入](docs/CUBEMX_IMPORT.md)

## 主要功能

- **编辑与构建**。C/C++ 代码补全、实时诊断和源码导航，提供 CMake 与 Verilog 编辑辅助。按工程锁定器件包、SDK 和工具版本，通过构建输出和内存分析查看实际结果。[工程工作流](docs/PRODUCT_WORKFLOWS.md) · [开发环境组件](docs/DEVELOPMENT_COMPONENTS.md)
- **烧录与调试**。按芯片和探针提供固件下载、源码与反汇编调试、寄存器查看和 FreeRTOS 状态。串口终端提供原始数据查看、协议解析和曲线绘图。[烧录](docs/DOWNLOAD.md) · [调试](docs/DEBUGGING.md) · [串口绘图](docs/SERIAL-PLOT.md)
- **AG32 MCU + FPGA**。在封装图上分配 MCU 外设和自定义 Verilog 端口，总线可逐位绑定。支持外设初始化代码生成、MCU 与 FPGA 联合构建、RTL 电路图、独立仿真波形页和静态时序报告。布局布线所需的 Supra 许可按厂商流程配置。[AG32 引脚映射与逻辑](docs/AG32_LOGIC_MODE.md) · [AG32 外设](docs/AG32_PERIPHERALS.md)
- **版本管理与扩展**。提供 Git 提交图、差异检查、GitHub 协作、插件和主题。内置 AI 助手及外部 MCP 客户端可使用工程工具，操作权限按会话管理。[GitHub 协作](docs/GITHUB_COLLABORATION.md) · [插件](docs/PLUGINS.md) · [AI 与 MCP](docs/AI_AGENT.md)

## 器件与工具链

| 系列或平台 | 工程支持 |
| --- | --- |
| AG32 | VF/VH 系列裸机与 FreeRTOS 工程，自定义 Verilog 与联合构建。[使用说明](docs/AG32_LOGIC_MODE.md) |
| STM32 / WCH CH32 | STM32F1/F4 的 HAL、SPL 与 FreeRTOS 模板，CH32 按子系列提供工程与调试配置。[STM32](docs/STM32_SERIES.md) · [CH32V203](docs/CH32V203.md) · [CH32V307](docs/CH32V307.md) |
| STC | 8 位 SDCC 工程及支持型号的 ISP / Mon51，AI8051U-32bit 与 STC32G 的 MX251 工程。[STC8](docs/STC8.md) · [Mon51](docs/STC-MON51-UI.md) · [MX251](docs/MX251.md) |
| PIC16 | CMake、Ninja 与 XC8 工程，PICkit3 下载和 MDB 调试按具体型号配置。专有工具从厂商获取。[使用说明](docs/PIC.md) |
| Espressif / Arduino | ESP-IDF 和 ESP8266 RTOS SDK 工程，支持指定 AVR 与 ESP32 目标的 Arduino 模板。[Espressif](docs/ESPRESSIF_SUPPORT.md) · [Arduino](docs/ARDUINO.md) |
| RP2040 / RP2350 | Pico SDK C 工程和 MicroPython 模板。[RP2040](docs/RP2040.md) · [RP2350](docs/RP2350.md) · [MicroPython](docs/MICROPYTHON.md) |
| PY32 / GD32 | 按子系列提供器件包和固件工程，适配范围见[普冉 PY32](docs/PY32.md)和[兆易创新 GD32](docs/GD32.md)。 |

具体模板、下载和实板调试范围见各系列文档。公开资源可从 [MCU StudioX MCUPacks](https://github.com/XieJunHui9566/MCU-StudioX-MCUPacks) 和 [MCU StudioX Toolchains](https://github.com/XieJunHui9566/MCU-StudioX-Toolchains) 获取。[在线器件包说明](docs/REMOTE_PACKS.md)

## 从源码构建

桌面端使用 C#、.NET 10 和 WPF。安装 [`global.json`](global.json) 指定的 SDK 后，在仓库根目录运行以下命令。

```powershell
.\tools\Build.ps1
dotnet run --project src/StudioX.Desktop
```

[架构说明](docs/ARCHITECTURE.md) · [工具链准备](docs/TOOLCHAINS.md) · [维护与回归](docs/MAINTENANCE_GATES.md) · [界面规范](UI_STYLE.md)

项目自有源码采用 [MIT 许可证](LICENSE)。第三方组件及素材沿用各自的许可。[第三方声明](NOTICE.md)
