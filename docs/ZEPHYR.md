# Zephyr MCU 模板

在新建工程页依次选择器件厂商、器件包、准确 MCU，再在“起始模板”中选择 **Zephyr**。框架选择与 HAL、SPL、Arduino 共用同一个入口。独立“Zephyr 实验模式”入口已移除；历史实验工程仍可打开和编辑。

## 支持范围与版本

首批交集为 99 个准确器件条目，来自 StudioX STM32 器件包 0.1.3 和 Zephyr **4.4.2**：STM32F100、F103、F105、F107、F401、F405、F407、F411、F413、F417、F423、F427、F429、F437、F439、F446、F469、F479。**同系列不代表所有容量均支持**；以选择器实际提供的模板为准。

器件身份、架构、Flash 与主 SRAM 的地址和容量必须与核验清单一致。上游没有明确配置、容量不符或未完成适配的型号不会显示此模板。GD32、WCH、RP 和 ESP 等其他系列尚未在此批次开放；不会因 StudioX 已有裸机或 Arduino 包就自动宣称 Zephyr 支持。

STM32F401xD 虽有上游 Kconfig / DTS 声明，但锁定版本的 HAL 目标宏无法通过实际编译，暂不开放；模板不会隐式改选其他容量目标。

锁定组件为 `zephyr.stm32 / 4.4.2`，包含 Zephyr 4.4.2、Zephyr SDK 1.0.1 / Arm GCC 14.3.0、Python 3.12.7 和所需 Python 依赖、CMSIS 与 STM32 HAL。不要求安装其他 IDE，不修改系统 PATH，也不安装全局 Python。缺少组件时使用“准备工程开发环境组件”导入相同身份的离线组件。

源码固定为 [Zephyr v4.4.2](https://github.com/zephyrproject-rtos/zephyr/tree/v4.4.2)，提交 `dccb09599635bdff17633fa7e9dab014b91dce90`。交集生成入口为 `tools/New-ZephyrDeviceCatalog.py`，记录各 SoC DTS / Kconfig 的 SHA-256。组件制作入口为 `tools/New-ZephyrToolset.py`，保留来源、完整文件哈希与上游许可证。

## 按自己的 PCB 调整

```text
CMakeLists.txt                           应用源文件与构建配置
prj.conf                                 内核和驱动选项
app.overlay                              外部器件与引脚覆盖
src/main.c                               最小线程示例
boards/others/studiox_<mcu>/board.yml      工程自带的板级身份
boards/others/studiox_<mcu>/*.dts          板级时钟、内存与外设
boards/others/studiox_<mcu>/Kconfig.*      引用上游 SoC
.studiox/project.json                    框架、器件包和组件版本记录
```

初始板级配置使用内部 HSI 时钟，F1 为 8 MHz、F4 为 16 MHz。模板不默认连接 LED、串口、外部晶振或其他板载器件，示例仅周期性休眠。请按电路图增加 GPIO、UART、I2C、SPI 等配置，并在 `prj.conf` 启用对应驱动。使用引脚复用前，需要显式包含实际封装对应的上游 pinctrl 文件；只含容量和引脚数的 MCU 名称不足以自动决定封装连线。

修改外部晶振或 PLL 时，同时核对电压范围、Flash 等待周期和总线限制。添加 C/C++ 文件后在 `CMakeLists.txt` 的 `target_sources` 中登记。工程内 DTS 和 overlay 可直接编辑；顶部“设备树”按钮打开板级 DTS。

## 编译与结果

点击 IDE 编译按钮，经过工程健康检查、完整组件校验和内容锁定后，调用官方 Zephyr CMake / Ninja 流程。源码复制到独立用户缓存中的临时工作区，支持工程目录包含中文和空格；组件或用户缓存路径需要可用的 ASCII / Windows 短路径。路径不满足约束时给出具体诊断，不修改系统短路径设置。

结果保存在 `.build/zephyr/`：`zephyr.elf`、可用的 BIN / HEX、`zephyr.map`、合成后的 `zephyr.dts`、`.config` 和原始 `build.log`。实际编译参数和生成头文件用于 C/C++ 代码提示。构建结束清除临时工作区；工程及板级配置在构建期间变化时，不发布旧结果。

当前提供软件工程生成、编辑与编译。下载和调试尚未接入，不能将其他开发板的下载配置自动套到用户 PCB 上；编译成功不代表实板验收。
