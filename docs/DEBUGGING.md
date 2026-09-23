# MCU 源码调试（OpenOCD / GDB）

工具栏「调试」按钮直接启动实机调试，当前接入器件目录内 STM32F1 / STM32F4 共 23 个子系列、244 个基础型号，使用 ST-Link 或 DAP-Link (CMSIS-DAP) / SWD、内置 OpenOCD 与 ARM GDB。器件包新建工程和 CubeMX CMake 导入工程共用该能力；HAL、SPL 及各自的 FreeRTOS 模板均可使用源码调试。寄存器、栈帧和变量在连接后从目标读取。开发用的离线模拟仍只对应「打开 F407 离线调试示例」，界面明确标明「未连接芯片」。

**STM32 实板验收目前仅覆盖 F407；其他 STM32 型号通过配置和协议离线检查，待有板后补充实测。** 完整覆盖与验证记录见 STM32 调试扩展验收。未收录型号不会凭 STM32 前缀自动放行，架构或 Flash/RAM 布局与目录不匹配也会在启动前拒绝。

## 实机操作

CH32V203 使用独立 `wch.ch32v203/0.1.0` 包，收录 11 个型号，按容量和 SDK 分支区分配置，V203 寄存器窗口排除 FPU。11 个型号通过编译和离线检查；C8T6 + WCH-LinkE 在用户授权解除读保护后通过 IDE 基础调试实板验收，见 C8T6 实板记录。范围和默认时钟见 V203 软件适配。

CH32V307VCT6 / RCT6 / WCU6 使用 `wch.ch32v307/0.1.1` 器件包、`wch.riscv/1.0.0` 工具集和 WCH-Link / WCH-LinkE，工作在 RISC-V / SDI 模式。当前只接受 256 KiB Flash / 64 KiB RAM 布局、400/4000/6000 kHz 和单台探针（序列号留空）。不支持把通用 RISC-V、ST-Link 或 CMSIS-DAP 配置套用到 CH32。更新 IDE 即可使用现有 0.1.1 包；0.1.0 工程没有下载/调试配置，不自动升级工程。

CH32 已在 VCT6 + WCH-LinkE 上通过附加、硬件断点、复位、运行/暂停、进入/逐过程/跳出、调用栈、局部变量和 SRAM 读取。寄存器窗口按 GDB 返回的编号读取整数、浮点及基础机器 CSR，排除厂商 GDB 泛列的向量/虚拟化寄存器。新工程默认只观察 `$pc`，可手动添加 `SystemCoreClock` 等工程符号。其他封装及特殊断点的实板范围见 CH32 调试验收。

AG32VF303CCT6 使用 `studiox.preview.ag32vf303 0.1.1` 包，烧录器选“AGM BLASTER（官方）”，走厂商专用 OpenOCD / RISC-V GDB。操作窗口和断点机制共用，寄存器按实际目标读取。该板与官方探针通过 USB 扩展坞完成 IDE 实机附加、固件校验、源码断点、单步、运行/暂停、寄存器、变量、只读 SRAM 和退出恢复验收，见 2026-09-23 IDE 实机记录。支持的 Flash/逻辑区布局见 AG32 适配记录；电脑 USB 直连的通信故障见 实机排查。

附加校验禁用 OpenOCD 的 RAM 工作区，使用主机读回比较，避免校验算法覆盖正在运行程序的全局变量。参见 [OpenOCD 工作区配置](https://openocd.org/doc/html/CPU-Configuration.html)。

1. 打开支持调试的工程，选择对应烧录器：STM32F1/F4 使用 ST-Link 或 DAP-Link (CMSIS-DAP)，CH32V203 / V307 使用 WCH-Link / WCH-LinkE，AG32 使用官方 AGM BLASTER。调试与下载使用同一份工程设置，速度和序列号在工具栏设置中修改；更换烧录器清除旧序列号并恢复新烧录器的默认速度。编译需要包含调试信息。
2. 使用「下载」按钮把当前工程写入板卡。**调试按钮自身不下载或擦除固件**；它先做增量编译，再连接并暂停目标，检查器件和板上程序是否与当前 ELF 一致。不一致时退出并提示先下载。
3. 点击「调试」或 Ctrl+F5，连接成功后使用断点、运行、暂停、单步、调用栈、变量与只读内存窗口。初次连接停在板上程序当前执行位置；如需从头执行，设置 main 内断点，再点「复位」「运行」。
4. 点击「结束」会移除调试器断点、分离目标、退出 GDB，并等待 OpenOCD 确认目标恢复运行，再释放 OpenOCD 和探针。如果清理或恢复运行无法确认，会明确报错，不显示成功结束。

DAP 使用内置 `interface/cmsis-dap.cfg`，保留 OpenOCD 的自动后端选择（USB bulk v2 / HID v1），不限定单一厂商的 VID/PID；参见 [OpenOCD CMSIS-DAP 配置](https://openocd.org/doc/html/Debug-Adapter-Configuration.html)。连接状态、会话标记和日志显示实际烧录器与芯片型号，不再固定显示 ST-Link。J-Link 下载入口保留，但尚未接入实机调试，点击调试时会在编译和连接前提示选择已支持的烧录器。

工程内源码/头文件/构建脚本的摘要及 ELF 摘要写入构建凭据，调试启动前核对；会话使用独立 ELF 快照。当前摘要不覆盖工程外引用文件，请先重新编译外部依赖。板上校验只读取装载映像，不修改 Flash、OTP 或选项字节。日志位于工程 `.build/download-*/debug-session.log`。

下载和调试通过用户级文件锁互斥，同一时间只允许一个 StudioX 探针会话。调试端口动态分配且只绑定回环地址；只连接本次 OpenOCD 明确报告已监听的端口。会话进程加入 Windows Job Object，IDE 异常退出时回收子进程。异常断线仍可能使目标暂停，界面不会声称芯片已恢复；重新连接后可复位或运行。

实机验收应通过 IDE 界面实际执行，不以命令行连接成功代替：断点命中、单步、变量/寄存器、条件与临时/日志断点，以及不重新上电的「结束 → 下载 → 再连接」。验收记录与当前通过项另存，不把未验证项计为通过。

## 使用

导入随发行版提供的 `studiox.stm32f407-0.1.1.mcupack`，选择「调试 → 打开 F407 离线调试示例」。安装目录有此包时会自动从本地导入。示例基于 F407ZG HAL 模板，在独立用户目录生成，不覆盖已有工程。示例源码已修改时，离线模型拒绝把它当成真实执行；重新打开一个示例即可。

- 左侧：编写代码时只显示工程页签；启动调试后显示寄存器页签，结束时收起。F1 显示 Cortex-M3，F4 显示 Cortex-M4 / FPU，变化值高亮。寄存器名称和编号由 GDB 返回，不对 F1 填充虚构的 FPU 数据。
- 中间：源码断点槽、黄色当前执行行、蓝色选中调用栈位置；单击左侧槽或右键设置断点。
- 底部：调试页签只在调试会话中显示；结束时若仍选中调试页，则切回构建页。其中调用栈与变量观察并排，另有局部变量、断点管理、只读内存、断点日志和 MI 原始输出页签。
- 顶部：运行、暂停、逐过程、进入、跳出、复位、刷新、结束；窄窗口自动换行。

| 操作 | 快捷键 |
| --- | --- |
| 启动实机调试 / 结束当前调试 | Ctrl+F5 |
| 运行 | F5 |
| 设置 / 删除断点 | F9 |
| 启用 / 禁用当前断点 | Ctrl+F9 |
| 条件 / 日志 / 临时断点设置 | Shift+F9 |
| 运行到光标 | Ctrl+F10 |
| 逐过程 | F10 |
| 进入函数 | F11 |
| 跳出函数 | Ctrl+F11 |

本轮未提供寄存器写入、任意表达式执行、外设 SVD、反汇编和 RTOS 线程识别。变量观察支持输入符号、成员路径、固定下标、寄存器名；离线模型只解析示例中的符号，其他项显示不可用。

## 特殊断点

右键源码左侧断点槽，或在光标行按 Shift+F9，打开断点设置；也可从底部「断点 → 条件 / 设置…」编辑所选断点。以下设置可以组合：

- **条件**：例如 `app_counter >= 3 && (app_input & 1) == 1`。留空为无条件。
- **跳过前 N 次到达**：只在首次设置或开始会话时初始化。先消耗跳过次数，再判断条件；继续运行不会重置次数。例如 N=2 时，前两次不判断条件，从第三次开始判断。
- **临时断点**：只有条件满足并真正触发后才自动删除；被跳过或条件为假时保留。
- **日志断点**：例如 `count={app_counter}, output={app_output}`，触发后在「断点日志」输出并继续。使用 `{{` 和 `}}` 输出花括号。单步到达时输出后仍暂停；表达式求值失败时保持暂停并显示原因。

当前条件和日志插值使用只读整数表达式，支持变量、`$寄存器`、十进制/十六进制常量、括号、算术/比较/逻辑/位运算及短路求值。离线模型按有符号 64 位整数计算，不模拟完整 C 类型提升规则；不支持赋值、函数调用、指针解引用、成员或数组下标表达式。输入错误在保存前提示，运行时错误不会静默跳过。

「运行到光标」仅在暂停时可用，创建独立的会话断点，不覆盖该行已有条件或日志规则。到达目标、遇到其他暂停、结束会话时清理，不写入持久化设置。目标行同时有日志断点时，会先记录日志再暂停。

普通断点为红圆；条件/计数断点增加白色中心；临时断点为橙色方块；日志断点为蓝色菱形。禁用和未绑定断点使用灰色或空心标记。底部断点表显示类型、规则、到达次数；悬停可查看剩余跳过次数和日志内容。

规则随工程持久化，到达次数不持久化。开始新会话或修改规则时重新计数。实时修改绑定失败会恢复原规则。暂不提供数据访问断点、函数名断点和地址断点。

实机硬件断点容量交由 OpenOCD 按芯片资源处理，不再固定为 F407 的 6 个。GDB 的 `hardware-breakpoint-limit unlimited` 表示客户端不额外限制数量，**不表示芯片有无限断点**；资源不足时保留服务器诊断。F407 离线模型仍用 6 个槽位测试资源耗尽和重试。参见 [GDB 远程断点限制](https://www.sourceware.org/gdb/current/onlinedocs/gdb.html/Remote-Configuration.html)。

## 生命周期与持久化

`StudioX.Application/DebugSessionService` 管理 Disconnected/Starting/Stopped/Running/Stopping/Faulted 状态、串行命令、项目隔离和异常恢复。运行期间禁止读目标与修改断点，窗口中的旧快照明确标识；停止后统一刷新。结束和关闭工程时清理寄存器、执行行、调用栈，忽略旧会话的迟到事件。

调试期间源码只读，构建、下载和工程文件修改受控；结束后恢复。关闭、切换工程沿用未保存文件确认，不隐式保存或覆盖用户工程。断点使用文本锚点随编辑移动，已保存文件才持久化行号，放弃编辑不移动持久断点。

断点与观察项存放在 `%LOCALAPPDATA%/MCUStudioX/debug/<工程路径摘要>.json`，不写入工程配置。示例工程位于 `%LOCALAPPDATA%/MCUStudioX/debug-examples/`。

## 后端边界

- `Engine/Debugging/GdbDebugAdapter`：标准 MI 命令映射、请求序号匹配、错误传播、寄存器/调用栈/变量/内存结果转换。
- `MiRecord`：嵌套 tuple/list、重复 frame 字段、转义和异常数据解析。
- `IGdbMiTransport`：离线 `SimulatedF407Transport` 与实机 `GdbProcessTransport` 共用 MI 解析。实机实现负责 token 响应配对、异步通知、超时、进程清理和会话日志。
- 特殊断点条件、忽略次数和临时属性映射到标准 MI `-break-insert` 参数；日志断点由主机收到停止事件后求值、输出并继续，不是目标端零开销跟踪。断点通知按顺序处理，用户暂停优先于日志自动继续。
- `OpenOcdDebugPlanner`：复用工程器件的 OpenOCD 脚本，按芯片和探针选择 ARM / 厂商 RISC-V GDB、SWD / SDI 及仅回环端口；计划没有烧录/擦除命令。CH32 使用厂商旧版 OpenOCD 的命令拼写和目标名称，不套用 Cortex-M 退出命令。

离线验证不代表实机验证。其它目标系列、不同 DAP 固件与 USB 后端、掉线重连、复杂优化代码和多线程 RTOS 场景，需要各自验证。实机验收记录只涵盖实际接入的探针与板卡。

## 验证

`tools/Build.ps1` 编译桌面与全部核心模块。

`dotnet run --project tools/StudioX.DebugValidation -- <F407包> <runtime目录> <新输出目录>` 检查 MI 解析、操作状态、断点命中与资源不足、调用栈/局部变量、观察项、只读 RAM、关闭清理和会话配置；还检查表达式、条件与计数组合、临时删除、日志插值和自动继续、求值错误、绑定回滚、运行到光标与暂停竞争。没有硬件进程执行。

`dotnet run --project tools/StudioX.DebugValidation -- --stm32 <STM32-0.1.1包目录> <runtime目录> <新输出目录>` 检查全部 244 型号、四种模板的目标选择，以及两类工程 × 两种探针的 976 份会话计划。用内置 OpenOCD `noinit` 解析配置、以假读数检查芯片 ID / 容量保护；在配置阶段 shutdown，不连接 USB、不执行固件。另检查 M3/M4 稀疏寄存器响应、完整订货后缀、错误架构/存储布局和内置 GDB 对断点限制命令的支持。这个模式不会编译 976 份固件，也不模拟这些芯片的执行。

`dotnet run --project tools/StudioX.DebugValidation -- --wch <CH32V307-0.1.1包> <runtime目录> <新输出目录>` 编译 VCT6/RCT6/WCU6 三种工程，用厂商 GDB 读取符号、OpenOCD `noinit` 解析配置，并检查目标/探针/布局边界、附加保护、默认观察项和寄存器编号过滤；不连接硬件。

`MCU StudioX.exe --preview-debug <新输出目录> <F407包>` 使用独立用户数据执行 WPF 交互与深浅主题/最小窗口渲染检查。

`MCU StudioX.exe --preview-breakpoints <新输出目录> <F407包>` 检查断点设置输入、规则显示、条件触发、日志输出、运行到光标、关闭清理，并生成深浅主题和最小窗口截图。

`MCU StudioX.exe --show-debug-demo <F407包>` 打开可操作的可见示例窗口。

`MCU StudioX.exe --show-breakpoints-demo <F407包>` 打开预置条件、日志和临时断点的示例。按 F5 后，前两轮输出日志，第三轮在条件断点暂停；继续运行可依次体验后续条件暂停和第四轮函数内的临时断点。

可追加第三个参数 `<独立用户数据目录>`，只为此次预览加载 F407 包，隔离演示产生的工程及设置；不会切换或修改正常启动使用的用户数据目录。正常启动也不再完整校验器件仓库。

设计与协议参考：[Keil 调试菜单与快捷键](https://www.keil.com/support/man/docs/uv4cl/uv4cl_ui_debug.htm)、[Keil 寄存器窗口](https://www.keil.com/support/man/docs/uv4cl/uv4cl_db_dbg_cpuregs.htm)、[GDB/MI 执行命令](https://www.sourceware.org/gdb/current/onlinedocs/gdb.html/GDB_002fMI-Program-Execution.html)、[OpenOCD 与 GDB](https://openocd.org/doc-release/html/GDB-and-OpenOCD.html)。

特殊断点语义参考：[GDB/MI 断点命令](https://sourceware.org/gdb/current/onlinedocs/gdb.html/GDB_002fMI-Breakpoint-Commands.html)、[GDB 条件与忽略次数](https://sourceware.org/gdb/current/onlinedocs/gdb.html/Conditions.html)。
