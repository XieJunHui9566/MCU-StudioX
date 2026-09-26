# AG32VF303CCT6 临时测试包

包 ID：`studiox.preview.ag32vf303`，版本 `0.1.1`，StudioX Pack 格式 1。仅提供一个明确型号 AG32VF303CCT6，不枚举未核实的其它型号。

用途：在 StudioX 界面中导入包、创建工程、编译，并通过官方 AGM BLASTER（DAP 模式）下载和调试。它不是原 VS Code 插件的兼容包，也不包含工具链。

## 模板

- `editor-demo`：中文注释、宏、枚举、结构体、函数与内存演示数据，方便查看和编辑代码。
- `minimal`：内部时钟与心跳循环，保留应用入口。

两个模板不配置 LED、串口或其它板级引脚。模拟数值不是真实传感器数据。

## 可选 Verilog 逻辑模式

创建 AG32VF303CCT6 工程时可单独启用 Verilog 逻辑模式，默认关闭。该模式面向本型号的 `AGRV2KL48` 逻辑器件；启用后才需要维护 `.ve` 引脚映射和自定义 Verilog。引脚位置须按实际 LQFP48 板卡核对，不能复制 100 脚示例的映射。

**逻辑开发需用户另行准备 Quartus II Full 与 AGM Supra。** 先将 `logic/pins.ve` 同步到 AGM AgRV SDK / PlatformIO 配套工程，按本机 SDK 的 `[setup_logic]` 配置 `logic_ve`、`logic_device = AGRV2KL48`、`ip_name`、`logic_dir` 并执行 Prepare LOGIC，再将生成接口与 `user_logic.v` 对齐；StudioX 当前没有 Prepare LOGIC 任务。之后 Quartus II 编译 Verilog 并转换出 `.vo`，Supra 再生成逻辑 `.bin`，按厂商的独立逻辑下载流程写入芯片。StudioX 当前提供构建和下载指引，不自动执行这两套软件或烧录逻辑区。内置 AgRV GCC 和普通“下载固件”按钮只处理 MCU 应用。软件版本、工作步骤和验收范围见 [AG32 Verilog 逻辑模式](../../../docs/AG32_LOGIC_MODE.md)。

新建工程采用分层 CMake：根 `CMakeLists.txt` 用于添加用户文件，`device/CMakeLists.txt` 管理 SDK 和芯片参数，`device/platform.cmake` 管理固定环境/产物规则。内部配置由 IDE 的工程生成器产生，在 IDE 中只读。详见 [工程分层](../../../docs/PROJECT_LAYOUT.md)。

## 器件资源

制作脚本从用户已有的 `framework-agrv_sdk` 1.0.0 拷贝头文件、启动汇编、系统调用、中断实现和 SVD，并从 SDK 链接片段生成独立链接脚本。没有复制编译器、OpenOCD 或其它可执行工具。

器件物理 Flash 为 256 KiB，RAM 为 128 KiB。本模板链接区域只使用 Flash 前 156 KiB，保留末尾 100 KiB 逻辑区域，与先前确认的 AG32 布局一致。编译参数记录 RV32IM AFC / ilp32f 的 AgRV ABI；工具集标识为 `agm.agrv` / `1.0.0`，编译器标识 `agrv-gcc-11.1.0`。StudioX 已内置这套专用工具，可按 F7 编译。

第三方源码保留原版权声明，来源信息见 `vendor/framework-agrv_sdk.json`。本包是利用本机 SDK 制作的临时开发材料，未作为公共发行包发布。最小模板已用内置 AgRV GCC 在隔离目录编译生成 ELF/BIN/HEX/MAP；随后按用户授权完成实板烧录、回读和心跳验证。逻辑区、选项字节保持不变。0.1.1 新增 IDE 下载与调试配置；本次调试适配只完成离线检查，尚未用实板验收。旧 0.1.0 工程不会自动获得硬件配置，需导入新包并新建工程。

## 下载与调试

只提供“AGM BLASTER（官方）”选项，默认 1000 kHz，使用 IDE 内置的 AGM 专用 OpenOCD / RISC-V GDB。支持通用调试操作、断点、调用栈、变量、内存和目标实际提供的寄存器。硬件断点数量由目标报告。调试先核对板上应用，不自动烧录；退出时恢复调试控制并确认目标运行。

当前仅支持前 156 KiB 应用 / 后 100 KiB 未压缩逻辑的已核实布局。不同逻辑布局、错误芯片或读保护状态会拒绝继续，不解除保护、不修改选项字节。详见 [调试配置来源](debug/provenance.md)。

## 重建

```powershell
.\tools\New-Ag32PreviewPack.ps1 -SdkDirectory '<本机 AgRV SDK 目录>'
```

脚本输出 `.mcupack`，不自动导入、不创建工程、不执行工具链或烧录。输出文件已存在时拒绝覆盖。SDK 和链接文件生成在 `.artifacts` 下，不将厂商代码复制进仓库源文件。
