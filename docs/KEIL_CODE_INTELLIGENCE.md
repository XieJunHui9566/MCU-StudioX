# Keil 移植工程的代码分析

Keil 插件复制原 Target 的源码、包含目录及宏，并生成 CMake 工程。移植后构建成功但出现大量未定义红线，是宿主仍采用器件包默认模板参数，未读取原工程的编译数据库导致的。

宿主识别 `.studiox/keil-import.json` 后，使用 `.build/compile_commands.json` 中每个翻译单元的实际工作目录、包含路径、宏、语言标准和 CPU 参数。支持 `command`、`arguments` 及有界的响应文件展开；不执行数据库中的编译器。器件模板参数不会覆盖原 Target。数据库、响应文件及移植标记变化时，撤销旧诊断并刷新分析。

缺少数据库时提示先配置或编译工程，不使用错误的模板参数猜测。原源码、工程锁及 `.clangd` 的禁用策略保持原有行为。CubeMX 使用同一数据库转换逻辑；普通器件包模板继续使用原分析配置。

验证包含真实 F103 SPL 与 F407 HAL Keil 工程的迁移、编译、原文件完整性、原始 main 诊断与头文件跳转，以及响应文件更新和真实未定义错误。验证报告位于 `artifacts/validation/keil-pack-repair-20261006`。

外部 CLI MCP 的会话授权方式及仍然保留的路径、哈希和设备检查见 [外部 MCP 接入](AI_MCP_EXTERNAL.md)。STM32 完整库发布及 CI 检查见 [器件包仓库](https://github.com/XieJunHui9566/MCU-StudioX-MCUPacks/tree/main/STMicroelectronics)。
