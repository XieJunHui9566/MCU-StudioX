# STC 仿真接入研究与旧实现复核

研究日期：2026-10-09。目标：IAP15F2K61S2，用户通常使用 CH340C。

## 结论与证据范围

StudioX 可以增加标准 8051/8052 软件仿真。当前 IAP15F2K61S2 / Monitor V2.5 已以 C# 接入 DeviceHub 和 IDE，完成持续事件、断点恢复、SDCC CDB 源码映射、变量/调用栈、进入/逐过程/跳出、条件/日志断点和反汇编。源码实板验收 17 项通过；操作与明确限制见 [调试界面](STC-MON51-UI.md)。旧 Python 和标准内核实验不作为产品依赖或 STC 外围模拟器。

前半段研究只读取旧工程、协议代码和历史日志，在隔离目录编译并执行模拟器。用户随后明确要求实机仿真，并确认芯片已设置为仿真芯片；本报告末节记录此次实板连接。旧项目的“真机验证通过”文字不是本次验收结果。当前 IDE 尚未增加 STC 仿真入口。

| 路线 | 执行位置 | 本次证据 | 后续边界 |
| --- | --- | --- | --- |
| uCsim 软件仿真 | PC 上的标准 C52 模型 | 编译、地址断点、指令单步、寄存器和内存读取通过 | 没有 STC15 模型；STC 1T 时序和专用外设需另行建模 |
| STC Monitor-51 | 已设置为仿真芯片的 IAP15，串口传送命令 | 当前 COM14 / V2.5 实板完成 216 字节固件读回、断点、10 次原生单步、调用/返回及运行/暂停 | IDE 后端和源码窗口尚未接入；不能扩展为其他型号/监控版本验收 |

## 厂商资料核对

[STC15 官方手册](https://www.stcmicro.com/datasheet/STC15F2K60S2-en.pdf)（2015-10-10 版，第 873 页）明确列出 IAP15F2K61S2 仿真资源：Flash 保留 `0xDC00–0xF3FF`，用户区最多 55 KiB；XDATA 保留 `0x0400–0x06FF`；占用 P3.0/P3.1。该型号共有 256 字节 IDATA 和 1792 字节 XDATA，因此该监控布局下用户片内 XDATA 只余 1024 字节。

[STC 官方串口仿真流程](https://www.stcai.com/newsinfo/4342789.html)说明先把目标设置为仿真芯片，再由 Keil 的 STC Monitor-51 Driver 通过串口调试，支持复位、运行、单步和断点。[官方工具说明](https://www.stcmicro.com/pdf/stc-tool-en.pdf)提供了对应的串口连接及驱动设置流程。

据此，CH340C 可以作为这条路线的 USB 转串口通道。ISP 下载通信和 Monitor 调试通信必须分别实现。监控安装、普通固件下载和调试下载的 Flash 布局也必须分别管理；不能沿用普通 IAP15 工程的全部 61 KiB 程序容量。

不同监控版本的协议、保留区和断点行为需建立版本配置。历史 V2.5 监控不能直接等同当前板的 ISP BSL 版本，也不能套用其他 STC 系列的 USB/SWD 调试功能。

## 旧工程实际内容

- [debug-mini 源码](E:/MCU/MCU_Save/C51/debug-mini/src/main.c)：计数并写 P1；`.mem` 记录 185 字节代码，使用 small 模型，计数变量在内部 RAM `0x08`。
- [debug-demo 源码](E:/MCU/MCU_Save/C51/debug-demo/src/main.c)：包含函数调用、计数、模式翻转和 P1 循环；`.mem` 记录 288 字节代码，使用 large 模型，计数在 XDATA `0x0001–0x0002`，模式在 `0x0003`。
- 两个目录都只有普通固件和编译产物，没有调试端通信代码及 `.cdb` 文件；源码调试依赖旧适配器对 `.map/.lst` 的解析。

相关实现实际位于旧仓库 `E:/MCU/MCU_for_VS/python/mon51.py`、`python/debugadapter.py`，逆向记录位于其 `docs/re/`。安装的旧 VS Code 扩展也包含这些代码。本次没有修改旧仓库或固件工程。

## 历史日志证明了什么

[mon51.log](E:/MCU/MCU_Save/C51/IAP15F2K61S2-SDCC/mon51.log)来自另一份 IAP15 SDCC 工程，共 579 行：206 次 TX、76 次 RX、297 次 PURGE。

- 有 4 次 `06 00 56 32 2E 35 04` 回复，即监控版本 `V2.5`。
- 31 个完整数据回复的全帧校验和有效，没有发现这些帧的校验和错误。
- 末段旧实现称作 GO 的命令后，寄存器回复重复显示 PC=`0xDBFD`；相邻 CODE 读取在该处包含 `02 00 4C`，即跳向启动代码的 LJMP。本次实测进一步确认该命令实际是设置复位入口 PC，DBFD 是 V2.5 的复位跳板，不能据此宣称程序已执行。
- 日志没有证明两份样例的实板断点、单步、用户变量及运行恢复都通过；也不足以确定激活失败缺少的最后一条命令。

这给出了可复用的帧格式和回复样本。后来结合官方驱动的运行/单步调用路径及当前实板，确认了旧实现的主要协议误解，见末节和独立协议文档。

## 离线复现的旧代码问题

复现使用原始代码和假目标，串口入口被显式禁止；共 6 类问题、10 个复现用例。证据为 [legacy-review.json](E:/STVAPP/MCU-StdioX/.artifacts/stc-simulation-research-20261009/legacy-review.json)。

| 问题 | 原始代码位置 | 实际影响 |
| --- | --- | --- |
| 继续在 0.25 秒后主动暂停，并无条件通知 breakpoint | `debugadapter.py:386,559` | 返回 PC 不在已设断点上仍报告命中；无法提供正常持续运行语义 |
| MOV direct,Rn / MOV Rn,direct 的长度被填为 1 | `mon51.py:94` | 0x88、0x8F、0xA8、0xAF 应为 2 字节；临时断点会落到操作数里。debug-demo 的 `0x00FE: 8E 82` 也受同类错误影响 |
| stepIn、stepOut 都直接调用 next | `debugadapter.py:618,621` | 界面命令能返回，不代表实现了进入函数或返回调用者 |
| 请求的下载失败后仍接受 launch 并报告 entry | `debugadapter.py:462` | 会以错误固件或不完整固件进入看似成功的调试会话 |
| HEX 解析没有验证记录校验和及地址扩展 | `mon51.py:635` | 离线复现确认错误校验和被接受，写入前缺少固件完整性门禁 |
| 两层断点操作不一致 | `mon51.py:698` / `debugadapter.py:300` | 客户端写 0xFE，适配器写 0xA5；不能把两者视为同一个已验证实现 |

另有两个必须处理的约束：

1. `_read_frame` 会删除异步 boot 帧，收发过程中频繁清空缓冲；真实停止通知可能被丢掉。新后端应持续解析字节流，将命令应答和停止事件分别派发，并保留未知帧。历史日志不能证明每次 PURGE 都丢了有效数据。
2. debug-mini 的 XDATA 上限为 1280，debug-demo 为 2048，都超过上述监控布局的 1024 字节用户上限。现有样例实际用量很小，这一配置错误尚不能解释它们当时的全部失败，但扩展工程后可能覆盖监控区。

旧笔记同时记载“全部调试通过”与“下载后 GO 仍停在 0xDBFD”，且断点码说法冲突。接入依据应以可重复的原始日志和准确的停止状态为准。

## 新的离线仿真验证

现有 `stc.sdcc/1.0.0` 工具目录已包含 uCsim 0.8.15。模拟器 SHA-256 与工具清单一致：`33396a0a8938981c99c7a90d26499544543e675a982565c5d3ff3c7513a399f8`。当前清单尚未为模拟器声明独立 executable role。

在隔离目录以 SDCC 4.5.0 `--debug` 编译标准 8051 探针，生成 `.ihx/.cdb/.omf`。通过 uCsim 的 TCP 命令控制台执行 C52 模型，验证了以下行为：

- 3 次命中探针函数断点；执行 3 条指令后 counter 从 0 变为 1；继续后 counter=2、total=3。
- 原样加载 debug-mini 的已有 HEX，命中 `0x00B0`，单步写 P1 后 counter=1、P1=1。
- 原样加载 debug-demo 的已有 HEX，命中 `0x00D5`；执行一轮后 counter=2、mode=1、P1=99。

合计 6 次断点停止，14 个内存值断言通过。使用有界指令执行推进到断点；持续全速运行/随时暂停的后台控制不包含在这份自动化验收中。交互控制台另外验证了 run 到断点。

证据：[simulation-result.json](E:/STVAPP/MCU-StdioX/.artifacts/stc-simulation-research-20261009/simulation-result.json)、[完整命令与回复](E:/STVAPP/MCU-StdioX/.artifacts/stc-simulation-research-20261009/tcp-console.json)。这证明了标准内核上的执行及工具可控性；不代表 STC 外设或当前实板验收。

复现命令：

```powershell
pwsh -NoProfile -File .artifacts/stc-simulation-research-20261009/Run-Probe.ps1 -LegacySamples 'E:/MCU/MCU_Save/C51'
& artifacts/tool-runtime/stc-isp-portable-3.14.7/python.exe -B .artifacts/stc-simulation-research-20261009/Review-Legacy.py 'E:/MCU/MCU_for_VS' 'E:/MCU/MCU_Save/C51/IAP15F2K61S2-SDCC/mon51.log'
```

## StudioX 接入建议

1. 提取与 GDB 无关的调试后端契约。当前 `DebugSessionService` 直接持有 `GdbDebugAdapter`，离线示例也是固定 F407 传输；不能直接承担 8051 指令仿真。保留现有窗口和工程断点管理，让后端报告能力、状态和真实停止原因。
2. 增加标准 8051 软件仿真后端及 SDCC 符号读取。根据 [SDCC 官方手册](https://sdcc.sourceforge.io/doc/sdccman.pdf)，`--debug` 生成 `.cdb` 和 `.omf`。源码行、变量位置、类型和优化后的可用性需要解析符号；HEX 本身不提供这些信息。明确区分 CODE、DATA、IDATA、XDATA、SFR、BIT 地址空间。
3. 以 C# 实现 Monitor-51 帧解析和会话状态机。复用经过日志验证的协议知识，保留超时/断帧/未知回复；不接入旧 Node 桥，不把 Keil 驱动 DLL 加载进 Desktop。串口仍由设备服务单一持有，下载、串口观察和调试竞争需要可观察的所有权交接。
4. 接入已验证的“连接 → 读取监控版本 → 暂停 → 寄存器/内存 → 下载读回 → 复位入口启动 → 断点/原生单步 → 恢复”闭环。型号来自准确工程配置及独立设备证据，不能把 `08 01` 回复当型号。对每步回读状态，不把 ACK、延时或 DAP success 当成执行成功。持续运行时等待停止通知，用户暂停才发送暂停命令。
5. 为准确型号和监控版本声明调试构建配置、保留区域及固件身份。断点要保存/恢复完整指令，区分条件跳转、调用、返回和中断；已停在断点时先恢复原指令、正确执行再重装。尚未实现的操作应报告不支持。

本轮已通过实板执行和驱动反汇编补全关键协议。以后核对其他监控版本时，可使用官方 Keil 成功会话桥接日志作对照；同时记录波特率、DTR/RTS、冷启动时点和时间戳，只抓 ISP 下载不能补全调试协议。监控安装及芯片配置变更仍需另行明确授权。

## 2026-10-09 实板连接记录

用户随后要求“试试实机仿真”，并确认芯片已设置为仿真芯片。本轮目标型号来自用户报告的 IAP15F2K61S2；USB 枚举确认 `USB\\VID_1A86&PID_7523`、CH340、COM14，串口转接器枚举本身不证明 MCU 料号。AiCube-ISP 进程仍在运行，但本探针能够独占打开 COM14，没有关闭或接管该进程。

隔离探针使用工程已有的便携 Python/pyserial，读取流程不调用旧客户端的下载、自动重连或软断点实现。这仅为协议实验，不作为产品运行时或 C# 后端的替代。

### 实际结果

- 115200、8N1、无流控，打开前 DTR/RTS 明确设为 false；没有主动翻转控制线。
- hello 回复 `06 00 56 32 2E 35 04`，监控版本 V2.5。
- 初始寄存器 PC=0x0000；CODE 及多个 RAM 读命令得到完整且校验有效的回复。初期沿用的 RAM 空间标签有误，下面保留原始帧，最终变量验证使用修正后的空间编号。
- 初步发送旧实现称为 GO 的 `02 00 00 00 00`，收到 `06 00 00 00 00 00 04`，随后 PC=0xDBFD。后来确认此命令实际设置复位入口 PC；它没有启动执行。初期 result.json 中 `xdataStart` 的标签不代表真正 XDATA，实际 SPACE=04 是 IDATA。
- 随后通过监控 CODE 读命令保存全部 55 KiB 用户区视图，SHA-256 为 `86F593CF2DAC33BA351E8FAEA5619A12141E52CBD7A60C2C3807B8ED19F7371F`。0x0003–0x01FF 为 FF，其他区域有大量非 FF 数据；不能只看入口附近就宣布整片为空。未与物理 Flash 读回对照，不把该视图声称为可恢复的物理备份。
- 明确固件写入授权前，三次读取会话共 458 个数据回复，校验失败数为 0；有一次复位入口 PC 操作，没有程序/配置写入及擦除命令。
- 该阶段关闭串口并保持暂停；之后用户明确同意写入选定固件，继续完成后续验证。

证据：[实板证据汇总](E:/STVAPP/MCU-StdioX/.artifacts/stc-mon51-hardware-20261009/evidence-summary.json)、[运行/暂停结果](E:/STVAPP/MCU-StdioX/.artifacts/stc-mon51-hardware-20261009/r3/result.json)、[运行/暂停原始收发](E:/STVAPP/MCU-StdioX/.artifacts/stc-mon51-hardware-20261009/r3/serial.jsonl)、[完整 CODE 读取](E:/STVAPP/MCU-StdioX/.artifacts/stc-mon51-hardware-20261009/r4/result.json)。日志 UTC 为主机收发时间，不是 MCU 指令执行时间。

### 新测试固件准备

已在隔离目录编译 [probe.c](E:/STVAPP/MCU-StdioX/.artifacts/stc-mon51-hardware-20261009/monitor-test/probe.c)，生成 SDCC CDB/OMF 和 [probe.ihx](E:/STVAPP/MCU-StdioX/.artifacts/stc-mon51-hardware-20261009/monitor-test/probe.ihx)：

- 216 字节，覆盖 0x0000–0x00D7；BIN SHA-256 为 `2DE74703C8FD2BCD3E444F0136C84DFB1F44DC862FD95F95CA2E37ED538C2CB6`。
- 只访问 RAM，无 GPIO、UART 或中断操作。counter 在 DATA 0x30，total 在 XDATA 0x0020–0x0021，checkpoint 在 CODE 0x00A8，main 在 0x00C4。
- 构建将用户 CODE 上限收紧到 0xDBFD 之前，额外保留历史 V2.5 跳板的 3 字节；用户 XDATA 上限为 1024 字节。HEX 记录长度、校验和、地址范围、重叠和结束记录均通过校验。
- 生成的 IHX SHA-256 与此前通过 C52 离线执行的探针完全一致。这是构建及标准内核证据，不是实板执行证据。
- 用户随后选择“允许写入并验证”。通过监控用户程序擦除命令及 6 个 CODE 数据块完成下载；216 字节逐字节读回及 SHA-256 完全一致，没有配置写入。

### 实机执行结果与旧协议错误

详细帧格式、字段布局及操作证据见 [IAP15 Monitor V2.5 协议](STC-MON51-PROTOCOL.md)。本次关键发现：

1. `02 00 00 00 00` 是设置复位入口 PC，实际运行是 `08 00`，暂停是 `00 02`。旧实现使用了错误的 GO 命令。正确运行后从 DBFD 的 `02 00 4C` 跳板完成 SDCC 启动，进入 checkpoint，不需要断电激活。
2. `08 01` 是原生指令单步，不是型号识别。10 次原生单步通过，包括两字节 MOV、三字节 MOV DPTR、MOVX、LJMP、LCALL 和 RET；这些单步命令之间不改写 CODE。
3. 19 字节寄存器数据的 0/1 是 A/B，10/11 是 DPH/DPL，14 是 PSW，15 是 SP。INC A 后 A=1、DPTR=0x0020 及 CALL/RET 的 SP=7→9→7，与驱动布局交叉验证通过。旧解析把 A/B、DPTR 和 SP/PSW 映射错了。
4. XDATA 的 SPACE 为 02，间接 IDATA 为 04，直接 DATA/SFR 为 01。旧实现把 04 当 XDATA。修正后 counter 在 DATA 0x30、total 在 XDATA 0x20 的读值与执行逻辑一致。

实测值：第一轮返回后 counter=1、total=1；第二次调用返回后 counter=2、total=3。持续运行后使用 `00 02` 暂停，PC=0x00B9，counter=180、total=30062。地址断点采用 A5，所有临时改写字节已恢复，完整固件再次读回一致；最后恢复测试固件运行并关闭 COM14。初次寄存器解释失败的 r7、RAM 空间解释失败的 r8 保留为失败记录，修正后的 r9/r10/r11 通过；没有把失败记录改写成成功。

证据：[单步与函数调用/返回](E:/STVAPP/MCU-StdioX/.artifacts/stc-mon51-hardware-20261009/r9-steps/result.json)、[完整启动与运行/暂停](E:/STVAPP/MCU-StdioX/.artifacts/stc-mon51-hardware-20261009/r10-run-pause/result.json)、[原生单步](E:/STVAPP/MCU-StdioX/.artifacts/stc-mon51-hardware-20261009/r11-native-steps/result.json)。每个目录同时保留 serial.jsonl。当前结果验证了该板及版本的串口调试协议；IDE 集成、源码级步进、完整外设显示、多断点/条件断点及其他监控版本尚未验收。
