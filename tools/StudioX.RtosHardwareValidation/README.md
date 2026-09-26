# FreeRTOS 硬件观测辅助程序

无参数只显示用法，不连接硬件。`--prepare` 只读取已有工程、器件/探针配置、工具锁、源码摘要和映像/ELF哈希，并写入指定输出目录的 `plan.json`。输出目录必须位于工程和工具运行时之外；运行时原地复用，不复制工具链或 SDK。

`--self-test` 仅对内存中的快照与日志行执行验收规则回归，不创建工程或连接硬件。它覆盖错误任务计数、TCB 对齐、缺失调度器状态、矛盾堆统计，以及 WCH 校验已报告差异但 MI 仍返回 `done` 的情况。

```powershell
dotnet run --project tools/StudioX.RtosHardwareValidation -- --prepare <工程> <runtime> <输出目录>
```

只有调用者已经明确选定板卡和板上固件，并决定进行硬件验收后，才使用 `--attach`。此模式只接受以下精确型号与探针的匹配组合，重新执行本地准备，准备失败时不连接：

| 工程器件 | 探针 ID | 配置验证 |
| --- | --- | --- |
| `STM32F407ZG` | `stlink` | 现有 STM32 目标和 ST-Link / SWD 配置 |
| `CH32V307VCT6`、`CH32V307RCT6`、`CH32V307WCU6` | `wch-link` | `WchDebugTarget.Find` 校验厂商工具、架构、Flash/RAM 布局和目标脚本；现有调试计划再验证 WCH-Link / WCH-LinkE、SDI、唯一探针声明和接口脚本 |

工程清单和器件配置的型号必须相同；不允许 STM32 使用 WCH-Link、CH32 使用 ST-Link，也不允许其它 WCH 型号借用 V307 的验收入口。

```powershell
dotnet run --project tools/StudioX.RtosHardwareValidation -- --attach <工程> <runtime> <输出目录> [逗号分隔的对象句柄符号]
```

硬件阶段通过真实 MCP 握手发现 `debug_rtos_snapshot`，依次附加核对现有 ELF、读取状态/内核快照、继续约 200ms、暂停等待并再次读取，最后结束恢复目标运行。它不编译、不下载、不擦除、不复位、不写内存、不新增断点。板上程序不匹配时保留现有附加错误和清理结果，不自动下载。

读取快照前还会独立检查本次原始会话日志：只有实际 GDB/OpenOCD 输出包含大于零的 `verified N bytes` 且没有实际地址 diff 或 USB 通信失败，才接受映像匹配；CRC checksum mismatch 若进入逐字节回退，随后完整核对成功且无 diff 可以通过。产品附加服务另外核对 verified 字节数与当前 ELF 装载映像字节数一致。MI `done` 和命令文本本身不作为证据。两份快照分别检查任务计数、任务列表去重和当前 TCB 对齐/归属、调度器字段，以及堆空闲、历史最低、最大块、块数与容量的一致性；容量上限来自本次器件 RAM 声明，不写死某个工程的任务数或堆大小。无法确认外部 RAM 堆时，本 helper 不把超过声明 RAM 预算的数据计为通过。严重矛盾使整体失败，原始快照和验收报告仍保留；可选字段未知则记录不完整项，Tick 未变化本身不单独判坏。

`result.json` 保留原始 MCP 内核结果、实际授权请求、Tick 观测差异、清理和探针锁可用性。`hardware=true` 表示执行的是显式硬件模式，`hardwareSessionVerified` 才表示附加与板上映像校验成功；没有可读内核时 `success=false`，清理完成可以独立记为 `observationCompleted=true`。不可读字段保留 `null`，Tick 不推进也保留观测，不强制判定内核异常。`debug-trace.log` 最多记录 1MiB 字符，过滤局部变量、函数实参和可能的凭据行，并报告遗漏行数；不复制工程内完整日志或源码。

附加、读取和控制异常均在 `finally` 中结束会话；清理失败不会报成功，原始异常及再次清理的异常都保留。`Disconnected` 仅证明应用会话结束；只有本次原始会话日志的 OpenOCD 输出包含 `STUDIOX_DETACHED_RUNNING` 时，`targetRestoredRunning` 才为真。启动脚本中的 `echo` 命令不计作恢复证据，辅助程序也不输出或复制完整日志。已完成附加后，缺少恢复标记或探针租约仍不可用均记录为错误。只有精确绑定工程的硬件附加与 continue/pause/stop 获有限授权，其它权限和工具一律拒绝。此辅助程序不是通用无人值守硬件入口。

## 本次 CH32V307VCT6 测试固件下载

用户另行明确允许本次测试固件烧录，并表示无需原固件备份或恢复后，可独立运行以下专用模式。它不随 `--prepare` 或 `--attach` 自动执行：

```powershell
dotnet run --project tools/StudioX.RtosHardwareValidation -- --download <工程> <runtime> <输出目录> <expected-BIN-sha256>
```

本专用模式仅接受当前 `CH32V307VCT6` / `wch-link` 配置，且预检 BIN 为 7984 字节、SHA-256 为 `407286CB1791CA1F78E8EDC46526A9CD6EA316C36C345FFF672B53E2A00FBF6D`。命令行 hash、真实 MCP `firmware_download_plan` 的 hash/型号/探针/速度/序列号必须一致，再给予单次 `firmware_download` 的有限授权；其他工具与权限均拒绝。这些固定值属于此次验收授权，不修改产品通用下载接口。

该模式复用产品的目标核对、映像擦写、校验及复位运行，原地使用已有运行时，不自动编译或附加调试。它写入 `download-result.json`，保留下载预检、审批摘要、成功/失败、原始日志路径；不复制完整下载日志。失败不自动重试、换固件或扩大目标范围。后续调试仍须由调用者单独执行 `--attach`。
