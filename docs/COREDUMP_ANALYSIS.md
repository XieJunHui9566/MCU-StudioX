# ESP-IDF CoreDump 分析

打开对应目标及锁定工具版本的 ESP-IDF 工程，在「固件故障分析」选择「转储 + 工程 ELF」或「转储 + 归档 ELF」。前者要求源码、工程配置、构建记录及 ELF 哈希一致；后者可选择历史 ELF，不要求伪造当前工程的构建凭据。

支持 Base64、原始二进制、ELF core，由用户明确选择格式。Base64 文件只包含正文，不包含 CORE DUMP START/END 或串口前缀，见 [Espressif 5.5.4 文档](https://docs.espressif.com/projects/esp-idf/en/v5.5.4/esp32s3/api-guides/core_dump.html)。解析不读取串口或 Flash，不下载 ROM，不执行用户 GDB/Python 启动脚本。

报告记录转储和 ELF 的 SHA-256、目标、IDF/解码器版本、崩溃任务、任务栈、实际 GDB 调用栈及原始诊断。带应用 ELF 摘要的转储必须匹配；部分转储仅保存摘要前缀，报告显示实际比较字符数。旧二进制格式可能没有摘要，此时显示未确认。转储目标不同时在启动 GDB 前拒绝。

输入先做独立快照：转储限 64 MiB，应用 ELF 256 MiB，输出 2 MiB，解析 2 分钟。失败保留原始输出并清除旧的可导出结果。重新导入报告只读取历史记录，匹配标记清除；重新选择原始转储才可复核。

工具包需要 gdb-esp32、gdb-esp32s3、gdb-riscv。原 5.5.4 组件没有 GDB 时明确报缺失，不退回系统 PATH。Prepare-EspressifRuntime.py 已纳入 SDK 推荐的 GDB 16.3_20250913；Prepare-EspressifDebugRuntime.py 可在新目录补齐本机工具及哈希索引，不覆盖安装或下载 SDK。

验收采用 [Espressif 官方测试仓库](https://github.com/espressif/esp-coredump/tree/d54b4a611c34efbc75cae1f862f58097f909261c/tests) 的 ESP32、ESP32-S3 已保存转储及对应 ELF，固定提交 d54b4a611c34efbc75cae1f862f58097f909261c，许可证 Apache-2.0。真实工具解码不代表本轮让用户 ESP32 板产生崩溃。来源及文件哈希保存在验收目录 provenance.json。
