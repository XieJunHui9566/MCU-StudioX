# MCU 源码调试（OpenOCD / GDB）

工具栏「调试」按钮直接启动实机调试，当前接入器件目录内 STM32F1 / STM32F4 共 23 个子系列、244 个基础型号，使用 ST-Link 或 DAP-Link (CMSIS-DAP) / SWD、内置 OpenOCD 与 ARM GDB。器件包新建工程和 CubeMX CMake 导入工程共用该能力；HAL、SPL 及各自的 FreeRTOS 模板均可使用源码调试。寄存器、栈帧和变量在连接后从目标读取。开发用的离线模拟仍只对应「打开 F407 离线调试示例」，界面明确标明「未连接芯片」。

**STM32 实板验收目前仅覆盖 F407；其他 STM32 型号通过配置和协议离线检查，待有板后补充实测。** 完整覆盖与验证记录见 STM32 调试扩展验收（本地记录）。未收录型号不会凭 STM32 前缀自动放行，架构或 Flash/RAM 布局与目录不匹配也会在启动前拒绝。

## 实机操作

CH32V203 使用独立 `wch.ch32v203/0.1.1` 包，收录 11 个型号，按容量和 SDK 分支区分配置，V203 寄存器窗口排除 FPU。新版为 V20x 的 10 个型号增加 FreeRTOS，CCT6 保留裸机模板。原有 11 个裸机型号通过编译和离线检查；C8T6 + WCH-LinkE 在用户授权解除读保护后通过 IDE 基础调试实板验收，见 C8T6 实板记录（本地记录）。新增 RTOS 仅有离线编译证据；范围和默认时钟见 [V203 软件适配](CH32V203.md)。

CH32V307VCT6 / RCT6 / WCU6 使用 `wch.ch32v307/0.1.3` 器件包、`wch.riscv/1.0.0` 工具集和 WCH-Link / WCH-LinkE，工作在 RISC-V / SDI 模式。当前只接受 256 KiB Flash / 64 KiB RAM 布局、400/4000/6000 kHz 和单台探针（序列号留空）。不支持把通用 RISC-V、ST-Link 或 CMSIS-DAP 配置套用到 CH32。原有 0.1.1 包的裸机工程仍可用；0.1.0 工程没有下载/调试配置，不自动升级工程。VCT6 的隔离 FreeRTOS 验收工程已通过 WCH-LinkE 实板 MCP 调试，见 RTOS 实板记录（本地记录）；RCT6/WCU6 仍只有软件证据，模板范围见 [模板说明](CH32V307.md)。

CH32 已在 VCT6 + WCH-LinkE 上通过附加、硬件断点、复位、运行/暂停、进入/逐过程/跳出、调用栈、局部变量和 SRAM 读取。寄存器窗口按 GDB 返回的编号读取整数、浮点及基础机器 CSR，排除厂商 GDB 泛列的向量/虚拟化寄存器。新工程默认只观察 `$pc`，可手动添加 `SystemCoreClock` 等工程符号。其他封装及特殊断点的实板范围见 CH32 调试验收（本地记录）。

AG32VF303CCT6 使用 `studiox.preview.ag32vf303 0.1.1` 包，烧录器选“AGM BLASTER（官方）”，走厂商专用 OpenOCD / RISC-V GDB。操作窗口和断点机制共用，寄存器按实际目标读取。该板与官方探针通过 USB 扩展坞完成 IDE 实机附加、固件校验、源码断点、单步、运行/暂停、寄存器、变量、只读 SRAM 和退出恢复验收，见 2026-09-23 IDE 实机记录（本地记录）。支持的 Flash/逻辑区布局见 AG32 适配记录（本地记录）；电脑 USB 直连的通信故障见 实机排查（本地记录）。

附加校验禁用 OpenOCD 的 RAM 工作区，使用主机读回比较，避免校验算法覆盖正在运行程序的全局变量。参见 [OpenOCD 工作区配置](https://openocd.org/doc/html/CPU-Configuration.html)。

1. 打开支持调试的工程，选择对应烧录器：STM32F1/F4 使用 ST-Link 或 DAP-Link (CMSIS-DAP)，CH32V203 / V307 使用 WCH-Link / WCH-LinkE，AG32 使用官方 AGM BLASTER。调试与下载使用同一份工程设置，速度和序列号在工具栏设置中修改；更换烧录器清除旧序列号并恢复新烧录器的默认速度。编译需要包含调试信息。
2. 使用「下载」按钮把当前工程写入板卡。**调试按钮自身不下载或擦除固件**；它先做增量编译，再连接并暂停目标，检查器件和板上程序是否与当前 ELF 一致。不一致时退出并提示先下载。
3. 点击「调试」或 Ctrl+F5，连接成功后使用断点、运行、暂停、单步、调用栈、变量、只读内存与反汇编窗口。初次连接停在板上程序当前执行位置；如需从头执行，设置 main 内断点，再点「复位」「运行」。
4. 点击「结束」会移除调试器断点、分离目标、退出 GDB，并等待 OpenOCD 确认目标恢复运行，再释放 OpenOCD 和探针。如果清理或恢复运行无法确认，会明确报错，不显示成功结束。

DAP 使用内置 `interface/cmsis-dap.cfg`，保留 OpenOCD 的自动后端选择（USB bulk v2 / HID v1），不限定单一厂商的 VID/PID；参见 [OpenOCD CMSIS-DAP 配置](https://openocd.org/doc/html/Debug-Adapter-Configuration.html)。连接状态、会话标记和日志显示实际烧录器与芯片型号，不再固定显示 ST-Link。J-Link 下载入口保留，但尚未接入实机调试，点击调试时会在编译和连接前提示选择已支持的烧录器。

工程内源码/头文件/构建脚本的摘要及 ELF 摘要写入构建凭据，调试启动前核对；会话使用独立 ELF 快照。当前摘要不覆盖工程外引用文件，请先重新编译外部依赖。板上校验只读取装载映像，不修改 Flash、OTP 或选项字节。日志位于工程 `.build/download-*/debug-session.log`。

下载和调试通过用户级文件锁互斥，同一时间只允许一个 StudioX 探针会话。调试端口动态分配且只绑定回环地址；只连接本次 OpenOCD 明确报告已监听的端口。会话进程加入 Windows Job Object，IDE 异常退出时回收子进程。异常断线仍可能使目标暂停，界面不会声称芯片已恢复；重新连接后可复位或运行。

实机验收应通过 IDE 界面实际执行，不以命令行连接成功代替：断点命中、单步、变量/寄存器、条件与临时/日志断点，以及不重新上电的「结束 → 下载 → 再连接」。验收记录与当前通过项另存，不把未验证项计为通过。

## 使用

导入随发行版提供的 `studiox.stm32f407-0.1.1.mcupack`，选择「调试 → 打开 F407 离线调试示例」。安装目录有此包时会自动从本地导入。示例基于 F407ZG HAL 模板，在独立用户目录生成，不覆盖已有工程。示例源码已修改时，离线模型拒绝把它当成真实执行；重新打开一个示例即可。

- 左侧：编写代码时只显示工程页签；启动调试后显示寄存器页签，结束时收起。F1 显示 Cortex-M3，F4 显示 Cortex-M4 / FPU，变化值高亮。寄存器名称和编号由 GDB 返回，不对 F1 填充虚构的 FPU 数据。
- 中间：源码断点槽、黄色当前执行行、蓝色选中调用栈位置；单击左侧槽或右键设置断点。
- 底部：调试页签只在调试会话中显示；结束时若仍选中调试页，则切回构建页。其中调用栈与变量观察并排，另有局部变量、断点管理、只读内存、反汇编、FreeRTOS、断点日志和 MI 原始输出页签。
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

本轮未提供寄存器写入、任意表达式执行和外设 SVD。变量观察支持输入符号、成员路径、固定下标、寄存器名；离线模型只解析示例中的符号，其他项显示不可用。FreeRTOS 的内核状态读取见下面的独立页签说明。

## 反汇编

启动调试后，在底部「调试 → 反汇编」查看地址、机器码、指令和函数内偏移；黄色 `▶` 标出暂停时的真实 PC。默认从 PC 向后读取 128 字节，勾选「跟随暂停位置」时，在该页每次暂停或单步后自动刷新。选择调用者栈帧不会把其返回地址冒充当前 PC；「选中栈帧」可单独浏览该帧地址，「当前 PC」回到执行位置并恢复跟随。

可输入十六进制地址并点「读取」。指令边界和 ARM Thumb / RISC-V 的不同指令长度由当前目标的 GDB 解析；手动地址应位于有效指令边界。没有源码行号时会显示该页，没有函数符号时仍可显示机器码与指令。无法访问的地址保留 GDB 原始诊断，在页内提示并记录于调试输出，不结束调试会话。

读取仅在暂停状态允许。运行中保留淡化的上次快照并取消 PC 标记；结束、断开或切换工程时清空，迟到响应不能覆盖新状态。F407 离线示例明确显示「离线模拟指令 · 非 ELF / Flash 实际内容」，不作为实机反汇编证据。

后端使用标准 `-data-disassemble -s <开始> -e <终点> -- 2`，单次范围为 1–512 字节，终点不包含且必须能表示为 32 位地址；此范围是单次响应大小，不限制会话调用次数。只读 MCP `debug_disassemble` 使用同一应用服务，支持默认 PC 或手动范围。参见 [GDB/MI 反汇编命令](https://sourceware.org/gdb/current/onlinedocs/gdb.html/GDB_002fMI-Data-Manipulation.html)。

## FreeRTOS 内核状态

调试目标暂停后，在底部「调试 → RTOS」读取内核快照，也可从「调试 → FreeRTOS 状态」菜单进入。视图使用现有 GDB 会话中的 ELF 调试信息和内存；不调用 `uxTaskGetSystemState()`、`vPortGetHeapStats()` 等目标函数，不修改内存，不额外连接探针。普通裸机工程显示「未找到可读取的 FreeRTOS 内核」，不会补出模拟任务。

| 区域 | 可读内容 | 条件与解释 |
| --- | --- | --- |
| 调度器 | Tick、调度器是否启动、挂起嵌套次数、当前 TCB、内核报告的任务数 | 暂停前的调度器状态；暂停期间 CPU 本身没有继续执行 |
| 任务 | 名称、TCB 地址、运行/就绪/阻塞/挂起/待就绪/待回收状态、当前与基础优先级、栈地址和保存的栈指针 | 根据内核任务链表与 TCB 类型读取；优先级继承可通过当前/基础优先级差异观察 |
| 任务附加信息 | 可取得的栈容量、未使用栈填充区、累计运行计数 | 相应字段及栈边界必须存在；计数单位来自固件配置，不能直接当作毫秒或 CPU 百分比 |
| 堆 | 可读取的总容量、当前及历史最小空闲量、成功分配/释放次数、最大空闲块及空闲块数 | 面向标准 `heap_4` / `heap_5` 符号；字段按实际存在情况读取，总容量与可分配容量可能含对齐/管理开销差异 |
| 队列与同步对象 | 名称、类型、当前计数/容量、单项大小、发送/接收等待任务数、互斥量持有者及递归次数 | 自动读取队列注册表；未注册对象可额外指定全局句柄符号或点分隔成员路径 |

不可读取的值显示为空或不可用，并保留 GDB 原始诊断；没有注册表或注册表为空不能说明工程没有同步对象。`heap_1`、`heap_2`、libc `heap_3`、自定义分配器、裁剪/改名的内核以及 SMP 内核不属于完整支持范围。没有足够符号时保留能够读取的部分，不把未知值显示成零。软件定时器、事件组、流/消息缓冲区尚不自动枚举；对象视图覆盖队列和以队列实现的信号量/互斥量。

建议固件保留内核 C 文件的 `-g` 调试信息，调试构建优先采用 `-Og` 或 `-O0`；优化、LTO 或 strip 可能使静态内核符号和类型不可见。使用 `-g3` 可保留 `portSTACK_GROWTH` 及栈填充选项等宏信息，便于确认栈水位扫描条件；无法确认方向或填充条件时不返回水位。无需移除 `static` 修饰符。以下选项应按所用 FreeRTOS 版本、内存预算和现有工程配置决定，IDE 不自动修改用户固件：

```c
/* 保留队列的对象种类字段，并初始化栈检查所需的填充值。 */
#define configUSE_TRACE_FACILITY             1
/* 保留栈最高地址，供调试器判断任务栈边界。 */
#define configRECORD_STACK_HIGH_ADDRESS      1
/* 按工程对象数量设置；创建后仍需主动注册句柄。 */
#define configQUEUE_REGISTRY_SIZE            16
```

创建需观察的队列或信号量后，在固件中调用 `vQueueAddToRegistry(sensorQueue, "sensorQueue")`；名称须具有足够长的存储期，例如字符串字面量。未启用 `configUSE_TRACE_FACILITY` 时，部分对象类型无法可靠区分，例如二值与计数信号量显示为通用信号量。官方内核的普通队列与队列集合共享类型值，不能唯一辨别时显示「队列/队列集合」并提供诊断。累计运行计数仅在固件启用并正确配置 `configGENERATE_RUN_TIME_STATS` 时存在；此视图不自动接管计时器。栈填充扫描只是一项历史使用线索，不能保证没有溢出，也不能替代固件的栈溢出检查。

快照只在暂停状态读取。若断点恰好停在内核链表或堆更新中间，计数和链表可能暂时不一致；诊断会标记读取不完整，建议单步退出该更新后重新读取，不能据此直接判定内存损坏。运行中仅保留标明过时的上次快照；结束、断开或切换工程后清空，迟到响应不覆盖新会话。取消读取在当前只读 MI 命令完整返回后生效，不中断正在接收响应的调试连接；继续、单步或结束前也会取消后续 RTOS 查询。当前任务的 TCB 栈指针是上下文保存值，尤其不应代替正在运行任务的硬件 SP。该功能不提供任意任务的寄存器恢复、调用栈切换、持续执行轨迹或确定的死锁判定。

内置 Agent 和外部 MCP 共用只读 `debug_rtos_snapshot`，可传 `objectSymbols`（如 `["sensorQueue", "app.busMutex"]`）补充未注册的全局句柄。只接受 C 标识符与点分隔成员路径，不接受函数调用、任意地址或指针表达式。返回暂停状态、`snapshot`、`hardware`、`simulated` 和证据说明；所有可选字段为空时应先看 `diagnostics`，不要让模型推断为零。

已在 STM32F407ZG / ST-Link 2000kHz 的既有 LVGL 固件上完成 FreeRTOS V10.3.1 实板 MCP 验收：读取三项任务状态、`heap_4`、注册队列 `TmrQ` 和继续/暂停后的 Tick 变化，退出后核对恢复运行标记及探针释放，见 F407 实板记录（本地记录）。该固件缺少栈末地址、运行时间计数和队列类型字段，相应不可读值保留为空。CH32V307VCT6 / WCH-LinkE 的 V10.4.6 试验固件另完成三项任务、栈水位、`heap_4`、队列、二值/计数信号量及未持有的普通/递归互斥量状态读取，见 CH32V307 实板记录（本地记录），其中保留初始映像校验误判及修复后拒绝不匹配固件的证据。持有互斥量、优先级继承、持续调度性能、SMP 和其它芯片/内核版本尚未实板验证。演示数据仍明确标明离线，不作为实板运行证据。数据字段和配置依据官方 [V10.4.6 tasks.c](https://github.com/FreeRTOS/FreeRTOS-Kernel/blob/V10.4.6/tasks.c)、[queue.c](https://github.com/FreeRTOS/FreeRTOS-Kernel/blob/V10.4.6/queue.c)、[heap_4.c](https://github.com/FreeRTOS/FreeRTOS-Kernel/blob/V10.4.6/portable/MemMang/heap_4.c) 和 [配置模板](https://github.com/FreeRTOS/FreeRTOS-Kernel/blob/main/examples/template_configuration/FreeRTOSConfig.h)。

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

- `Engine/Debugging/GdbDebugAdapter`：标准 MI 命令映射、请求序号匹配、错误传播、寄存器/调用栈/变量/内存/反汇编结果转换。
- `MiRecord`：嵌套 tuple/list、重复 frame 字段、转义和异常数据解析。
- `FreeRtosInspector`：依据 GDB 调试类型读取内核链表、任务、堆与注册对象，检查链表循环和地址边界；不硬编码 ARM / RISC-V 的结构偏移，不执行目标函数。`DebugSessionService.ReadFreeRtosAsync` 与其它调试操作共用串行门和暂停状态守卫。
- `IGdbMiTransport`：离线 `SimulatedF407Transport` 与实机 `GdbProcessTransport` 共用 MI 解析。实机实现负责 token 响应配对、异步通知、超时、进程清理和会话日志。
- 特殊断点条件、忽略次数和临时属性映射到标准 MI `-break-insert` 参数；日志断点由主机收到停止事件后求值、输出并继续，不是目标端零开销跟踪。断点通知按顺序处理，用户暂停优先于日志自动继续。
- `OpenOcdDebugPlanner`：复用工程器件的 OpenOCD 脚本，按芯片和探针选择 ARM / 厂商 RISC-V GDB、SWD / SDI 及仅回环端口；计划没有烧录/擦除命令。CH32 使用厂商旧版 OpenOCD 的命令拼写和目标名称，不套用 Cortex-M 退出命令。

离线验证不代表实机验证。其它目标系列、不同 DAP 固件与 USB 后端、掉线重连、复杂优化代码和多线程 RTOS 场景，需要各自验证。实机验收记录只涵盖实际接入的探针与板卡。

## 验证

`tools/Build.ps1` 编译桌面与全部核心模块。

`dotnet run --project tools/StudioX.RtosValidation -- artifacts/tool-runtime <小输出目录>` 使用本机 ARM / WCH GDB 读取独立构造的静态 ELF，检查任务状态、栈水位、heap_4、队列/信号量/互斥锁、缺失字段、坏链、取消和只读命令。未执行内核，也未接入硬件。

`MCU StudioX.exe --preview-rtos <小输出目录> <F407包>` 验证裸机内核不可用、句柄输入、深浅主题与最小窗口、运行中的过时快照、暂停自动读取及结束/工程切换清理。界面截图中的 RTOS 数值明确标为离线样例，不是实板数据。

`dotnet run --project tools/StudioX.DebugValidation -- --disassembly` 检查 ARM Thumb 与 RISC-V 的 2/4 字节指令、MI 转义、无符号地址、缺少符号、空响应、原始错误及范围边界；不需要器件包或硬件。默认完整离线会话还检查 PC、手动地址、调用者选择和运行/断开/结束守卫。

`dotnet run --project tools/StudioX.McpValidation -- --rtos` 通过真实 MCP 握手检查 RTOS 工具定义、裸机不伪造内核数据、对象表达式拒绝、暂停/结束/工程隔离守卫，以及只读调用不申请写入授权；只使用隔离的 F407 模拟会话，不启动 GDB/OpenOCD 或访问硬件。

`dotnet run --project tools/StudioX.DebugValidation -- <F407包> <runtime目录> <新输出目录>` 检查 MI 解析、操作状态、断点命中与资源不足、调用栈/局部变量、观察项、只读 RAM、关闭清理和会话配置；还检查表达式、条件与计数组合、临时删除、日志插值和自动继续、求值错误、绑定回滚、运行到光标与暂停竞争。没有硬件进程执行。

`dotnet run --project tools/StudioX.DebugValidation -- --stm32 <STM32-0.1.1包目录> <runtime目录> <新输出目录>` 检查全部 244 型号、四种模板的目标选择，以及两类工程 × 两种探针的 976 份会话计划。用内置 OpenOCD `noinit` 解析配置、以假读数检查芯片 ID / 容量保护；在配置阶段 shutdown，不连接 USB、不执行固件。另检查 M3/M4 稀疏寄存器响应、完整订货后缀、错误架构/存储布局和内置 GDB 对断点限制命令的支持。这个模式不会编译 976 份固件，也不模拟这些芯片的执行。

`dotnet run --project tools/StudioX.DebugValidation -- --wch <CH32V307-0.1.1包> <runtime目录> <新输出目录>` 编译 VCT6/RCT6/WCU6 三种工程，用厂商 GDB 读取符号、OpenOCD `noinit` 解析配置，并检查目标/探针/布局边界、附加保护、默认观察项和寄存器编号过滤；不连接硬件。

`MCU StudioX.exe --preview-debug <新输出目录> <F407包>` 使用独立用户数据执行 WPF 交互与深浅主题/最小窗口渲染检查，包含反汇编 PC 标记、手动地址、页内错误、自动刷新、运行快照与结束清理。

`MCU StudioX.exe --preview-breakpoints <新输出目录> <F407包>` 检查断点设置输入、规则显示、条件触发、日志输出、运行到光标、关闭清理，并生成深浅主题和最小窗口截图。

`MCU StudioX.exe --show-debug-demo <F407包>` 打开可操作的可见示例窗口。

`MCU StudioX.exe --show-breakpoints-demo <F407包>` 打开预置条件、日志和临时断点的示例。按 F5 后，前两轮输出日志，第三轮在条件断点暂停；继续运行可依次体验后续条件暂停和第四轮函数内的临时断点。

可追加第三个参数 `<独立用户数据目录>`，只为此次预览加载 F407 包，隔离演示产生的工程及设置；不会切换或修改正常启动使用的用户数据目录。正常启动也不再完整校验器件仓库。

设计与协议参考：[Keil 调试菜单与快捷键](https://www.keil.com/support/man/docs/uv4cl/uv4cl_ui_debug.htm)、[Keil 寄存器窗口](https://www.keil.com/support/man/docs/uv4cl/uv4cl_db_dbg_cpuregs.htm)、[GDB/MI 执行命令](https://www.sourceware.org/gdb/current/onlinedocs/gdb.html/GDB_002fMI-Program-Execution.html)、[OpenOCD 与 GDB](https://openocd.org/doc-release/html/GDB-and-OpenOCD.html)。

特殊断点语义参考：[GDB/MI 断点命令](https://sourceware.org/gdb/current/onlinedocs/gdb.html/GDB_002fMI-Breakpoint-Commands.html)、[GDB 条件与忽略次数](https://sourceware.org/gdb/current/onlinedocs/gdb.html/Conditions.html)。
