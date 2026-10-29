MX251 V0.0117 研发构建入口

Project.cmake 管理 CMake INTERFACE 目标源码/头文件/宏；Ninja 每次调用 BuildLatest.ps1，
再由 Build.ps1 使用锁定 MX251 工具、启动与运行库。MX251_BUILD_SCRIPT 指向本目录
Build.ps1；显式传入 MX251_PROFILE、MX251_TOOLCHAIN、MX251_PWSH。目标/模型/运行库
在 mx251.json 中保存，不能用它替代 CMake 源码管理。此入口不安装到当前 IDE。

TargetContracts.json + TargetProfile.ps1 共用于 STC32G12K128 与 AI8051U-34K64-32，
不再为AI复制一份改目标字符串的构建脚本。两者仍有各自 profile，不共用启动文件。
profile lock schema 2 必须引用本目录 TargetContracts.json 的确切哈希和精确target ID，
锁定启动哈希、编译器overlay及实际使用的库。运行库准入独立于通用251指令支持。
AI 准入 standard、native-int、native-math-wide、native-int-mdu32、
native-math-wide-mdu32；其中 large 仅 standard/native-int-mdu32，原生数学仍限small。
STC 保留之前集合；不能把准入集合等同于所有组合实板验收。见 validation/acceptance.json。

启动事实记录区分 bit 与 SFR：AI CLR bit AF(EA)；STC MOV sfr A8(IE),0 和 AF(IE2),0。
本轮不修改这些启动指令，也不把 STC 的62向量策略套用到AI。qualifiedMemory 是现有
实验布局：CODE FF0000..FFFFFF；STC XDATA 10000..11FFF，AI XDATA 10000..16FFF
（恢复R89/V0.0100完整工程窗口，不能套用给STC）；IRAM参数100H、栈400..7FF，
栈上限检查1000H；不是整芯片物理容量或完整Flash资格说明。
receipt schema 4 记录目标规则哈希、启动哈希、向量与布局、实际编译器/profile等身份。
旧实验 schema 1 profile 被拒绝；封存版本仍能使用各自历史脚本重建，不原地改历史。

只允许工程 .build/mx251/latest。未知已有目录保留并拒绝；已拥有目录在独占文件租约内
核对绝对路径和链接后重新构建，只保留最新产物。失败清理固件和receipt，保留原始日志。
build.lock 文件保留，不代表仍有活动进程，独占句柄才代表所有权。
这是全量重建和受限定 CMake 工程，不是通用 CMake 原生 C 驱动或逐对象增量支持。
配置阶段失败发生在包装器之前，强杀后的不明目录与IDE生命周期统一仍需继续处理。
工具不安装全局环境，不写绝对开发者工具路径。复制到安装目录不等于产品验收通过。


MX251 V0.0120：受限叶函数反馈，默认关闭，非 LST
0013-leaf-call-feedback.patch 基于 V0.0119 有效源码，沿用 GPL；只改变 main.c、
wide.c、wide_emit.c.inc，新增 wide_leaf_effects.c.inc。没有引入 C251 源码。
按 source-verification.json 命令应用补丁：git -c core.autocrlf=false -c core.eol=lf apply。
源码保留各文件实际换行；完整源码重建输入锁和最终 compiler SHA 均已核验。

使用 CMake -DMX251_LEAF_CALL_FEEDBACK=ON 显式开启；默认 OFF。CMake 管理源码、
头文件和宏，Ninja 调用 MX251。第一遍生成已接受宽函数的保守 may-write；只选当前
导出且唯一、无调用、无内存写入的叶函数。第二遍重新编译 C 单元。只删除整组不冲突
保存，同时考虑 callee、调用者实参准备、结果搬运/清栈；SPX 位移重新发射。再次核对
实际影响及指令/字节代价，冲突或代价不合格保留原候选，不直接删除汇编文本。
来源绑定编译器、profile、输入快照、第一遍 ASM/REL/LST 和最终 callee 完整正文、
ABI、may-write；失效拒绝发布固件。不能用陈旧 sidecar 跨构建偷省第一遍编译。
这是 may-write 上界，不要求证明净 clobber；不把 callee 内保存恢复视为不破坏。
未知/legacy/运行库/汇编/间接调用和递归边不消费摘要。递归函数内部对普通叶函数的
直接调用可获益。原生回调路径仍按既有整函数回退，无新增间接调用优化。
底层 --mcs251-leaf-effects 文件是显式可信实验契约；直接传伪造掩码不受保证。
构建驱动的哈希用于失效与误混检测，不是防恶意修改构建工具/汇编的安全边界。

RunAcceptance.py 默认使用 acceptance.json；--leaf-call-feedback 使用独立锁定的
acceptance-leaf-feedback.json。两种模式各 18 完整镜像 + 6 拒绝，精确校验 HEX、
收据、profile、编译器与反馈来源。ON 的 8 份变化镜像有本版双目标实板证据，其他
10 份逐字节继承已封存基线；OFF 全部等于 V0.0119。22 边界组、9 失效检查通过。
STC32G 两模型直接/回调各 2560 结果 + 中断；AI 两模型回调和固定调用/协议通过。
两板已恢复原自测并释放租约。没有 Flash 回读，下载 ACK 与运行采集分别记录。
静态实际收益：calls 双模型各 -100B/-25 对 PUSH/POP；回调与 AIcalls 各 -48B/
-12 对；协议 -16B/-4 对。MOV、显式 SPX 字节/字访问、ABI 转换和 spill 减少均为0。
详细计时和真实函数见 mx251-v0.0120/report.html，单项提升不能替代完整应用结论。
