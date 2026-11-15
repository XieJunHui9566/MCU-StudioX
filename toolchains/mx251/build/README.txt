MX251 V0.0107 构建诊断增量

InterruptDiagnostics.ps1 仅翻译原始 ASlink 中断契约错误，输出代码/槽/符号/中文说明。
它不扫描源文件，不判定链接成功，也不抑制其它原始工具诊断。
interrupt-diagnostics-adapter.patch 应用于 V0.0106 的开发 adapter/Build.ps1；
把 InterruptDiagnostics.ps1 放在同一 adapter 目录。完整可复现入口在
.artifacts/ai8051u-sdcc251-20261023/compiler-optimization/mx251-v0.0107。
仍由 CMake 管理 sources/include/defines，Ninja 调用锁定的 MX251 构建入口。
补充诊断随失败状态保存；失败不发布 firmware.hex、linked.partial.hex 或 receipt.json。
JSON 写入用 InputObject 保持单元素诊断数组形状，原始 stdout/stderr 不变。

这是开发入口，不是 IDE 安装更新。开发 Build.ps1 仍要求新的输出目录，
不接管未知已有目录；IDE 的 latest 生命周期管理未在本增量中修改。
从冻结父版本移植时必须核对补丁/编译器/profile 哈希，不安装全局环境。


V0.0110新增BuildSource.py：输入完整有效source-state，及显式host-deps/GCC/Gawk/CMake/Ninja路径；新输出目录从零构建sdcc、sdas251、sdld。命令说明用 --help。
仅宿主子进程环境使用指定工具；不下载、不安装、不修改系统PATH。配置头属于输入源码快照，不声称自动适配任意宿主。无旧对象/归档输入。
完整标准库/原生运行库源码重建、成员清单、外部工具锁和审阅源码ZIP见本版封存目录repro与report.html；源码ZIP不是IDE发行包。
本次验证了两个干净目录的编译器/汇编器/链接器EXE相同，以及独立运行库重建14归档相同；MDU归档与父版相同。

V0.0111 增加 qualified/ 研发入口，解决重复 latest 构建与锁定依赖身份，详见其 README.txt。上文“拒绝已有输出”仍适用于底层单次 Build.ps1；CMake 通过新包装器管理受控 latest。未安装 IDE。


2026-10-28：新增独立 BuildNativeFloat.py / BuildNativeFloat.txt 源码构建入口。
显式指定源码、编译器、配套工具、CMake、Ninja和新输出目录，保持原64成员配方、
ABI桥与源码，不读取旧运行库归档。两次干净重建逐成员一致，原始日志及输入内容锁可追溯。
候选库必须核对完整 receipt 与编译器/源码/工具哈希，再绑定到隔离 profile；
不能在编译器升级时不说明运行库来源，或把旧库直接描述为获得了新后端的优化。

V0.0130运行库刷新仍是未验收候选，正式开发基线保持V0.0129。额外六个变化库成员
缺少本轮目标覆盖，且发现继承的cosh负输入符号缺陷；主机源码复现不等于目标实板。
此入口自身通过构建和失败保护验证，不授予数学语义、实板、LST或安装替换资格。
详细证据见compiler-optimization/mx251-v0.0130/report.html。
