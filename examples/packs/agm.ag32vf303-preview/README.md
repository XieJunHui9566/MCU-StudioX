# AG32VF303CCT6 历史验收夹具

本目录保留 `0.1.1` 单型号 manifest、模板和已经核实的 OpenOCD 目标，供历史验收复现及当前系列生成器复用。它不是当前器件包生成配方，不再生成或分发旧版本包。当前四个子系列、七款精确型号及重建入口见 [AG32 系列器件包](../agm.ag32-series/README.md)。

包 ID：`studiox.preview.ag32vf303`，版本 `0.1.1`，StudioX Pack 格式 1。仅提供一个明确型号 AG32VF303CCT6，不枚举未核实的其它型号。

用途：在 StudioX 界面中导入包、创建工程、编译，并通过官方 AGM BLASTER（DAP 模式）下载和调试。它不是原 VS Code 插件的兼容包，也不包含工具链。

## 模板

- `editor-demo`：中文注释、宏、枚举、结构体、函数与内存演示数据，方便查看和编辑代码。
- `minimal`：内部时钟与心跳循环，保留应用入口。

两个模板不配置 LED、串口或其它板级引脚。模拟数值不是真实传感器数据。

## 基础引脚映射与可选 Verilog

所有 AG32VF303CCT6 模板默认生成 `logic/pins.ve`，将 MCU 内部 GPIO、外设功能绑定到真实 LQFP48 封装引脚；目标固定为 `AGRV2KL48`。初始映射只含注释，须按真实板卡填写，不能复制 100 脚示例。基础模式用内置 AGM VE / Supra 构建和下载独立镜像，无需 Quartus。旧工程须显式启用，已存在的 `.ve` 保留原字节。

可选自定义 Verilog 模式默认关闭；启用后额外创建 `logic/user_logic.v`，这部分**仍需外部 Quartus II Full 与 AGM Supra 综合**。先在 AGM 配套工程按 `[setup_logic]` 配置 `logic_ve`、`logic_device = AGRV2KL48`、`ip_name`、`logic_dir` 并执行 Prepare LOGIC，再将生成接口与自定义 Verilog 对齐。StudioX 基础映射流程拒绝自定义模块，不能用默认网表代替用户设计。内置 AgRV GCC 只处理 MCU 应用；映射与 MCU 固件是分别构建和下载的产物，改变 `.ve` 后只下载 MCU 固件不会更新引脚。详见 [AG32 基础引脚映射与自定义逻辑](../../../docs/AG32_LOGIC_MODE.md)。

新建工程采用分层 CMake：根 `CMakeLists.txt` 用于添加用户文件，`device/CMakeLists.txt` 管理 SDK 和芯片参数，`device/platform.cmake` 管理固定环境/产物规则。内部配置由 IDE 的工程生成器产生，在 IDE 中只读。详见 [工程分层](../../../docs/PROJECT_LAYOUT.md)。

## 器件资源

制作脚本从用户已有的 `framework-agrv_sdk` 1.0.0 拷贝头文件、启动汇编、系统调用、中断实现和 SVD，并从 SDK 链接片段生成独立链接脚本。没有复制编译器、OpenOCD 或其它可执行工具。

器件物理 Flash 为 256 KiB，RAM 为 128 KiB。本模板链接区域只使用 Flash 前 156 KiB，保留末尾 100 KiB 逻辑区域，与先前确认的 AG32 布局一致。编译参数记录 RV32IM AFC / ilp32f 的 AgRV ABI；工具集标识为 `agm.agrv` / `1.0.0`，编译器标识 `agrv-gcc-11.1.0`。StudioX 已内置这套专用工具，可按 F7 编译。

第三方源码保留原版权声明，来源信息见 `vendor/framework-agrv_sdk.json`。此处记录最初使用本机 SDK 制作的单型号开发材料：最小模板已用内置 AgRV GCC 在隔离目录编译生成 ELF/BIN/HEX/MAP；随后按用户授权完成实板烧录、回读和心跳验证，逻辑区、选项字节保持不变。`0.1.1` 加入下载与调试配置后的后续实板结论见 [AG32 IDE 调试验收](../../../docs/AG32-IDE-DEBUG-ACCEPTANCE-20260923.md)。这些记录不扩大为其它型号的硬件验收。

## 下载与调试

只提供“AGM BLASTER（官方）”选项，默认 1000 kHz，使用 IDE 内置的 AGM 专用 OpenOCD / RISC-V GDB。支持通用调试操作、断点、调用栈、变量、内存和目标实际提供的寄存器。硬件断点数量由目标报告。调试先核对板上应用，不自动烧录；退出时恢复调试控制并确认目标运行。

当前仅支持前 156 KiB 应用 / 后 100 KiB 未压缩逻辑的已核实布局。不同逻辑布局、错误芯片或读保护状态会拒绝继续，不解除保护、不修改选项字节。详见 [调试配置来源](debug/provenance.md)。

## 当前生成入口

```powershell
python tools/New-Ag32Packs.py --sdk-directory '<本机 AgRV SDK 目录>' --platform-directory '<本机 AgRV 平台目录>' --output '<新输出目录>' --cli '<已编译 StudioX.Cli.dll>'
```

该命令生成当前四个子系列的新格式 `.mcupack`，并保留来源和文件 SHA-256。输出目录必须不存在；它不自动导入、创建工程、执行 Supra 或烧录。旧 `New-Ag32PreviewPack.ps1` 只报告迁移说明并停止，不再写包。完整参数及离线准备方式见 [系列配方](../agm.ag32-series/README.md)。
