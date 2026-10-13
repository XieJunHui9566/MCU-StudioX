# 开发与测试产物清理（2026-09-22）

按用户要求，清理项目测试阶段生成的单片机工程、重复 SDK、旧预览发行目录和废弃构建产物。此次只处理已确认由开发验证生成的内容；用户在仓库外的实际工程、已安装器件包、外观和串口偏好保留。

实际删除 443 项目录/文件，共 1,014,407 个文件，合计 93,189,677,908 字节（86.79 GiB），无删除失败。最近工程列表移除 9 条测试记录，保留 3 条用户工程，并移除对应的 4 份测试调试偏好。体积为被删除文件的逻辑大小，不包含保留的归档。

## 清理范围

- `.artifacts/` 中的 AG32、STM32、CH32 编译/下载/调试测试工程，CubeMX 导入副本、验证夹具、候选包解压目录、安装器临时 payload，以及测试时复制的 SDK 和开发环境组件。
- `artifacts/MCU StudioX * Preview/` 的 12 份过期预览发行目录，以及其他临时 UI 验证输出。
- 旧 0.1.0 器件包输出、已被子系列包替代的独立 `studiox.stm32f407zg` 包，以及失败/未采用的 SDK 下载副本。
- `examples/F407-Serial-Terminal/` 的完整串口/绘图测试固件及复制的厂商库，共 333 个 Git 跟踪文件。此目录不是 IDE 构建依赖；源码仍可从清理前提交 `6e480b7` 追溯，没有改写 Git 历史。
- 生成的 F407 离线演示工程、对应的最近工程记录与调试偏好，以及验证工具的可再生 `bin/obj`、旧 Desktop Debug 和旧 `win-x64` 构建副本。

最新 Release 开发程序保留在 `src/StudioX.Desktop/bin/Release/net10.0-windows/`。

## 保留内容

- 正式 `src/`、当前包生成配方、模板、许可证、插件/协议示例，以及仍用于回归验证的工具源码。
- `artifacts/tool-runtime` 的四套开发环境组件、`artifacts/language-runtime`、`artifacts/git-runtime`。
- 25 个当前器件包：23 个 STM32 子系列包、AG32 0.1.1 和 CH32V307 0.1.1。
- `artifacts/releases/` 的已生成安装包，以及 `.artifacts/installer-tools` 的 Inno Setup 编译器。
- `.artifacts/stm32-sources` 中当前配方所用的 F1 DFP、F4 SPL 1.8.0 与型号 PDSC；这些是重建输入，不能因位于临时目录而视为废弃库。

## 备份与历史验收记录

清理前复制并逐文件核对 SHA-256：

- `artifacts/hardware-backups/20260922/`：111 份硬件备份、回读、实际测试固件副本及相关诊断资料，包含 F407 原板 Flash、AG32 Flash/选项区及 CH32 原 Flash。`manifest.json` 保存原路径、新路径、长度和哈希。零字节或不完整的历史读取结果仅作为诊断记录，不视为可恢复固件。
- `artifacts/verification-archive/20260922/test-evidence.zip`：2,989 份日志、截图、串口采集和验证结果；压缩后逐条解压流核对 SHA-256。`manifest.csv` 保存每项原相对路径、长度和哈希。
- `artifacts/cleanup-20260922/`：清理前体积统计、完整删除清单、执行结果、被移除的用户测试记录备份，以及清理后的构建和依赖检查日志。

此前验收文档中的 `.artifacts/...`、旧预览目录及串口测试工程路径均表示**验收发生时的位置**。对应测试工程不再保留，已有日志和截图从上述 ZIP 内按原相对路径查找；硬件备份按 `manifest.json` 定位。文档中的测试结果不因目录清理而扩大验收范围。

## 清理后检查

- 443 个删除目标均已不存在；`.artifacts/` 只保留安装器编译工具与当前 STM32 SDK 输入。
- Release 全量构建通过，0 警告、0 错误，日志为 `artifacts/cleanup-20260922/build-after.log`。
- 23 个 STM32 包及 CH32 包与发行索引中的 SHA-256 一致，AG32 当前包保留。
- 四套开发环境组件清单中的 60,182 个文件均存在；各自 GCC、GDB 和 OpenOCD 的版本命令退出成功。clangd、Git 与 Inno Setup 输入保留，记录为 `dependency-check.log`。
- 111 份硬件/诊断归档再次逐文件核对 SHA-256，通过。

附加清理 `%LOCALAPPDATA%/MCUStudioX/language-cache` 中测试工程索引缓存的命令被自动审批拦截（仅返回 `blocked by policy`，未提供具体原因），没有执行。这部分缓存保留；不影响已删除的工程文件、已清理的最近工程记录或构建结果。

## 清理后的维护约定

临时单片机工程放入 `.artifacts/`，不把生成项目和复制的厂商 SDK 纳入 Git。原板备份放入 `artifacts/hardware-backups/`；验收结束保留必要证据，删除工程副本。正式 SDK 输入、器件包和开发环境组件只维护当前所需版本；用户已安装的历史包可能仍被外部工程引用，不随工作区清理自动卸载。
