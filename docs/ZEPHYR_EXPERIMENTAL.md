# Zephyr 实验模式

当前实现用于**离线导入专用包、选择板级目标、创建和打开工程，以及查看和编辑板级设备树源文件**。首个样板是用户确认的正点原子探索者 V3.5（STM32F407ZGT6）；Zephyr 版本固定为 **v4.4.2**。IDE 的 Zephyr 构建运行时尚未接入，工程中的构建、下载和源码调试入口保持禁用。此功能不改变现有裸机、ESP-IDF 或 STM32CubeMX 工程的流程。

## 包与工程

Zephyr 包沿用 `.mcupack` 扩展名，内部是独立的 `studiox.zephyr-pack` 格式 1。根清单为 `zephyr-manifest.json`，文件索引为 `files.sha256.json`；普通 StudioX 格式 1 包使用 `manifest.json`。两个包仓库分别位于用户数据目录的 `zephyr-packs/` 和 `packs/`，导入时按所选入口明确校验，不根据扩展名猜测格式。Zephyr 清单必须标记 `experimental: true` 并给出板目标、板修订、Zephyr 版本和模板文件映射。包中只有应用模板和板定义，不包含 Zephyr 源码、SDK、编译器或固件。

本地包配方在 [探索者 V3.5 示例](../examples/packs/zephyr.alientek-explorer-v35/README.md)，生成脚本为 `tools/New-ZephyrExperimentalPack.py`。需要制作可导入的包时，在仓库根目录执行：

```powershell
python .\tools\New-ZephyrExperimentalPack.py --output <输出目录>
```

脚本生成 `zephyr.alientek-explorer-v35-0.1.1.mcupack`，不获取 SDK，也不连接调试器。当前实验包位于 `artifacts/packs/Zephyr-Experimental-0.1.1/zephyr.alientek-explorer-v35-0.1.1.mcupack`，SHA-256 为 `4DA2532DAB00575E98686DCB36C98B2A70A8DCCED69A734110E42E54D1BC34D8`。v0.1.1 相对 v0.1.0 调整 OpenOCD 板级 runner 和板级说明；`blinky` 运行语句未改。包内提供完整 Apache-2.0 `LICENSE`；借用并改写的上游板定义保留原版权标识，模板把 `LICENSE` 一并复制到工程。项目使用包中的 `blinky` 模板，并在 `.studiox/project.json` 记录 `kind: Zephyr`、准确板目标和包内容哈希；`.studiox/zephyr-pack.json` 保留创建时的专用包清单。应用目录中包含 `CMakeLists.txt`、`prj.conf`、`src/main.c` 和工程内的 `boards/` 板定义。创建前会重新校验已安装包的文件集合和 SHA-256。工程不写入开发机器的绝对工具路径。

## 在 IDE 中创建

1. 打开欢迎页的“新建工程”，选择 **“Zephyr 实验模式…”**。
2. 点击 **“导入 Zephyr .mcupack…”**，选择上述脚本生成的包。普通格式 1 包不能从此入口导入，Zephyr 包也不能作为普通器件包导入。
3. 选择“探索者 V3.5 / STM32F407ZGT6（实验）”、`LED 闪烁（实验）`，填写工程名，再选择父目录。IDE 创建独立工程和 Git 仓库，然后打开源码。工程名对应的目标目录必须尚不存在。
4. 打开 Zephyr 工程时，IDE 自动展开工程树中的 `boards/` 路径并打开板级 `.dts`。工程树上方的“设备树 · 板级 DTS（实验）”可随时返回该文件；`.dts`、`.dtsi` 和 `.overlay` 可在编辑器中按设备树语法显示和编辑，右侧“文件结构”列出当前源文件的节点与属性并支持跳转。该入口读取工程创建时记录的模板文件映射，不固定依赖探索者的目录名称。

这里展示的是**工程内的板级 DTS 源文件**及其当前文件节点层级；它引用外部 Zephyr 工作区的 SoC 和引脚复用 `.dtsi`，这些上游节点不会被离线轮廓解析器展开。完整合成树 `zephyr.dts` 仅在 West 构建后生成；IDE 当前不自行合成，也不把板级源文件标为完整结果。

生成的板目标是 `alientek_explorer_f407zg`。已将上述 v0.1.1 归档交给 `ZephyrPackRepository.ImportAsync`，再通过 `ZephyrProjectService.CreateAsync` 创建工程；这个 **StudioX 生成的工程**通过了真实离线编译。在已准备 Zephyr v4.4.2 工作区环境后，从生成的工程目录运行：

```text
west build -b alientek_explorer_f407zg . -d <构建输出目录> --pristine always
```

该次环境为 Zephyr **v4.4.2**（提交 `dccb09599635bdff17633fa7e9dab014b91dce90`）、Zephyr SDK **1.0.1** 的 `arm-zephyr-eabi`、West **1.5.0**、Python **3.12.7**；SHA-256 为 `4DA2532DAB00575E98686DCB36C98B2A70A8DCCED69A734110E42E54D1BC34D8` 的最终 v0.1.1 归档经 C# 服务导入、生成工程后，构建命令退出码为 0，Ninja 完成 **155/155** 步，报告 Flash **16,900 B**、RAM **4,480 B**。生成工程位于被忽略的 `.artifacts/zephyr-workspace/studiox-generated-v011-current/hello/`，ELF 位于 `.artifacts/zephyr-workspace/build-studiox-v011-current/`，原始日志为 `.artifacts/zephyr-workspace/build-studiox-v011-current.log`。该次 BIN SHA-256 为 `0ADFA1B5B89BB3DFFCB8D865B4BA89C95864539444ADBDB00E75EDC966FA6C79`，与用户此前明确选择烧录的 v0.1.0 构建固件逐字节相同；验收见[实板记录](ZEPHYR_HARDWARE_ACCEPTANCE_20260928.md)。IDE 的 Zephyr 构建运行时仍未接入；内置的 STM32 裸机 OpenOCD/GDB 和构建后端不能用于此工程。`west flash`、`west debug` 涉及板上程序，不能把已连接调试器视为自动烧录许可。

Windows 上另一次对 **StudioX 生成工程**的验证遇到 West 1.5.0 跨盘路径错误：在 `E:` 盘工作区执行 `west build` 并传入 `C:` 盘工程的绝对源路径时，West 的 `_sanity_check_source_dir()` 在配置前因 `os.path.relpath` 报 `ValueError: path is on mount 'C:', start on mount 'E:'`。改为在 `C:` 盘该工程目录内运行、用 `.` 作源路径，构建输出仍放在 `E:` 盘，便完成 **155/155** 步。无需为此复制工程；这是本次 West 路径处理的观察结果，不表示 IDE 创建工程失败。

外部构建还验证了一个路径限制：Zephyr **v4.4.2** 的工程源码路径含空格时，即使模板 CMake 已正确引用路径、`ZEPHYR_BASE` 不含空格，仍会在 `kconfig.cmake:332` 报 `File not found .../path`。原始日志为 `.artifacts/zephyr-workspace/build-app-space-final.log`。复现实验构建时请让 Zephyr 工作区与工程源码路径均不含空格；这不表示 IDE 不能打开含空格路径中的工程。

## 探索者板级假设

用户确认实物为**探索者 V3.5**。所给文件名为 `EXPLORER_V3.5.pdf`，但六页图纸的标题栏均标 `Revision: V3.4`。以下连接从该图纸读取，作为实验板定义；本次只在实板上核验了 LED0/PF9，其他引脚仍待核对。图纸未随包分发。

| 项目 | 实验板定义与图纸连接 |
| --- | --- |
| 主控与时钟 | STM32F407ZGT6，外部 8 MHz 晶振；PLL 目标 168 MHz，APB1 42 MHz、APB2 84 MHz。 |
| LED | LED0 PF9、LED1 PF10，低电平点亮；`blinky` 使用 PF9，其中 LED0/PF9 已通过实板验证，LED1 尚未验证。 |
| 按键 | KEY0 PE4、KEY1 PE3、KEY2 PE2 低有效；WK_UP PA0 高有效。 |
| 串口 | USART1 PA9 TX / PA10 RX，板载 CH340C 经 P10 两组跳线连接；跳线实际安装状态未知。 |
| 调试 | 外接 ST-Link，经 SWD 的 PA13、PA14 和板上调试插座；本次复位保持附加通过，现有 runner 需要 NRST 接线。 |

2026-09-28 最初曾用已连接的 ST-Link 做**只读识别**：探针固件为 `V2J46S7`，目标供电约 3.27 V，SWD 读出器件 ID `0x413`、1 MiB Flash、Cortex-M4，符合 STM32F405/F407/F415/F417 家族。这不能单独证明完整料号或 PCB 修订。该次识别本身没有下载、擦除、复位或修改选项字节。随后用户明确授权烧录上述 BIN；两次一致的原 Flash 备份、烧录后完整回读、用户目视 LED0 闪烁、PF9 寄存器采样、OpenOCD/GDB 硬件断点和单步均记录在[实板验收记录](ZEPHYR_HARDWARE_ACCEPTANCE_20260928.md)。USART1、CH340C/P10 跳线、其它 LED 与按键尚未验证。

实验包 v0.1.1 的 OpenOCD runner 面向 Zephyr SDK 1.0.1，使用 `stlink-dap.cfg` 和 `dapdirect_swd`，以 `connect_assert_srst` 经 NRST 附加；GDB 连接后执行 `monitor reset halt`，再刷新寄存器缓存。该流程**会复位并暂停目标**，不能把 `west attach` 理解为对运行态完全无影响。硬件 `west attach` 断点和单步日志来自 v0.1.1 说明与注释修订前的归档；最终归档的 `board.cmake`、`openocd.cfg` 与实测时逐字节相同，最终 BIN 也与板上固件同 SHA-256，并已完成重新导入、生成和完整编译。原始调试日志见实板记录。这验证了外部 West/OpenOCD/GDB 路径，不代表 IDE 内 Zephyr 调试入口已接入。

目前没有 ESP32 Zephyr 包；待明确板型后再分别建立板目标。其它 STM32F407 及以上板型也不能直接套用探索者的引脚配置。

Zephyr 原理与命令参见[官方入门文档](https://docs.zephyrproject.org/latest/develop/getting_started/index.html)、[板级移植文档](https://docs.zephyrproject.org/latest/hardware/porting/board_porting.html)和[West 构建/下载/调试说明](https://docs.zephyrproject.org/latest/develop/west/build-flash-debug.html)；这里的工程版本仍锁定 v4.4.2，`latest` 文档可能描述更新中的版本。
