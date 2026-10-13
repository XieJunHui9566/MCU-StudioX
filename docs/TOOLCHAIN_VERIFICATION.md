# 内置开发环境组件验证 / 2026-09-21

## 重复构建的工具校验优化

在同一台机器上，使用 `TEST_SystemConfig`（STM32F103C8 / SPL）的隔离副本及 `arm.gnu/1.0.0`（17,275 个索引文件），分别测量优化前后两次真实构建。下表为第二次、源码无改动的构建；不是所有项目的保证耗时。

| 阶段 | 优化前 | 优化后 |
|---|---:|---:|
| 开发环境组件检查至 CMake 开始 | 7.22 s | 0.09 s |
| 完整无改动重复构建 | 7.45 s | 0.31 s |

两边固件 BIN 的 SHA-256 均为 `5975b6d98ff645169f2c609f35d6f7c49121ce61f8e36ec8095a13cb04f31155`。首次校验仍执行完整哈希；不把操作系统文件缓存导致的首次耗时差异算作稳定收益。

17 项隔离检查覆盖首次/复用/强制检查、编译器不匹配、文件元数据变化、DLL 内容损坏、脚本缺失、多余文件、清单指纹变化、UTF-8 BOM、取消、校验期间变化，以及新服务重新全检。只生成小型合成工具树，不运行其中的文件或访问硬件。

可复用入口：`dotnet run --project tools/StudioX.ToolsetValidation -- --self-check <new-directory>`；真实构建计时使用 `--build-benchmark <toolsets-root> <source-project> <new-directory>`。结果：`.artifacts/toolset-cache-before/timings.json`、`.artifacts/toolset-cache-after/timings.json`、`.artifacts/toolset-cache-boundaries/result.txt`。

## 桌面和发行目录

- `tools/Build.ps1` 编译通过，0 警告、0 错误。
- `tools/Publish.ps1` 生成 Windows x64 自包含便携目录 `artifacts/MCU StudioX Toolchain Preview`，包括三套完整工具及独立插件宿主。
- 三套工具的全部 43,287 个索引文件通过 SHA-256 校验；GCC/G++/GDB/CMake/Ninja/OpenOCD 均能启动。
- Arm Cortex-M4、RISC-V RV32IMAC C/C++ 探针通过头文件、编译和标准库链接检查。RISC-V nosys 库报告未实现系统调用的预期警告；该探针不用于硬件运行。
- AG32 最小工程完成 ELF/BIN/HEX/MAP 生成。将同一工程从开发工具目录切换到带空格的发行工具目录后，CMake 缓存重建及再次编译通过。
- 自包含应用以只含 Windows 系统目录的 PATH 启动，并故意注入无效 GCC_EXEC_PREFIX、CC、CMAKE_TOOLCHAIN_FILE、PYTHONHOME；桌面构建仍通过。
- 桌面检查验证构建前自动保存，以及 `#error` 的原始诊断返回界面。仅修改隔离副本，未修改用户工程。

界面结果：`.artifacts/toolchain-ui-publish/result.txt`、`build-success.png`、`tool-inventory.png`。测试中恢复了隔离副本源码；最后一次诊断日志包含刻意注入的错误。

## 经授权的 AG32 实板检查

用户明确选择「已编译的 AG32 最小测试固件」。使用便携版中的 AGM OpenOCD 及本机厂商 `agrv2k.cfg`，CMSIS-DAP v2 / SWD / 1 MHz。

| 项目 | 结果 |
|---|---|
| 指定器件 | AG32VF303CCT6 |
| 实读器件 ID | `0x40200001` |
| Flash 备份 | 262,144 字节，写入前完成 |
| 最小固件 BIN | 1,304 字节，地址 `0x80000000` |
| 实际擦写影响 | 首个 4 KiB 扇区，其余 Flash 逐字节未变 |
| 回读校验 | 固件 1,304 字节完全匹配 |
| 逻辑保留区 | `0x80027000` 起 100 KiB，SHA-256 不变 |
| 选项字节 | `0x81000000` 起 256 字节，SHA-256 不变 |
| 复位运行 | 应用读到正确器件 ID；心跳 `672693 → 1384715`，间隔约 500 ms |

固件 SHA-256：`081952428DEC3AFD6CCF1CED1A730082588541F344EB811929CCF462761B761C`。

原 Flash 备份 SHA-256：`7086CDFB5B9DF66E83C228EEC6E60E1F385C92EF9523F1282A29C767C2010560`。

完整备份、前后回读、日志和 `verification.json` 保存在 `.artifacts/toolchain integration/`；`flashed-minimal.bin` 是实际写入的不可变测试副本。模板仅运行内存心跳，不控制 LED 或串口。没有整片擦除、修改逻辑区或选项字节，也没有验证其他外设。

本次实板检查通过命令行完成。桌面烧录按钮、目标下载配置的包契约以及 GDB 调试界面仍需接入；不能把该结果当成通用器件烧录功能已完成。
