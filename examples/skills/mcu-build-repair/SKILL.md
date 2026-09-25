---
name: mcu-build-repair
description: 在 MCU StudioX 工程中定位 C/C++、汇编、链接或 CMake 编译错误，并用最小改动修复后验证。
---

# MCU 构建排错

适用于用户要求编译、解释错误、修复构建失败或检查编译产物的任务。只在当前绑定工程内工作。

1. 调用 `project_info` 确认准确器件、模板、编译器和工具集；如需确认芯片能力，使用 `device_info` 核对已安装包。不要根据型号字符串猜测时钟、内存或编译参数。
2. 先检查已有构建日志。用 `project_build_log` 从偏移 0 开始按需翻页，保留首个实际错误及其前后诊断。若没有日志，或用户要求重新编译，再调用 `project_build`；该操作需要 StudioX 对本次调用授权。
3. 对照诊断使用 `project_search`、`project_list_files` 和 `project_read_file` 阅读相关源码。区分编译、汇编、链接、配置和工具链问题；不要只凭最后一行 `build failed` 推断原因。
4. 提出最小修复，说明涉及的文件与原因。修改现有文件前用 `project_read_file` 取得当前 SHA-256，再调用 `project_edit_file`；新文件用 `project_create_file`。写入须经宿主逐次确认，失败时保留原始错误。
5. 修复后若用户允许构建，再运行 `project_build` 并用日志核对。报告实际退出码、产物和剩余错误；没有运行编译就明确标明“未编译验证”。

不要把编译成功当作硬件功能验证。不要隐式连接、下载或烧录 MCU。不要改写锁定的工具路径或生成文件来掩盖错误。
