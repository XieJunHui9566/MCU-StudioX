# STM32F407 FreeRTOS 实板只读调试验收（2026-09-26）

本次在已存在且经板上读回校验匹配的 LVGL 固件上完成实际 MCP 附加、暂停快照读取、短暂继续/暂停、再次读取及退出恢复。范围为 STM32F407ZG / ST-Link、FreeRTOS V10.3.1、`heap_4` 和注册的定时器命令队列；不把结果扩大为所有 FreeRTOS 版本或对象的实板验收。

## 工程、板卡与映像

| 项目 | 本次配置或证据 |
| --- | --- |
| 工程 | `E:\MCU\C\LVGL`，`studiox.stm32f407/0.1.1`，`hal-freertos` |
| 器件 | IDE 配置为 `STM32F407ZG`；原理图 U14 标注 STM32F407ZGT6，HSE 为 8MHz，SWD 接口与此次探针对应 |
| 探针 | ST-Link / SWD，2000kHz，未指定序列号；执行前由主调试代理确认系统唯一对应的 STM32 ST-Link |
| 目标识别 | 日志报告 device id `0x10076413`、Flash 1024KiB；ST-Link 报告目标电压约 3.266V（原值 3.266349V），不是示波器或独立电压仪器的实测证据 |
| 开发环境组件 | 已有 `arm.gnu/1.0.0`，编译器标识 `arm-gnu-15.2.rel1`；原地复用运行时，未复制 SDK |
| 内核 | GDB 实际返回 `V10.3.1` |
| ELF | 当前工程 `.build/firmware.elf`；SHA-256：`CE8C7169F28991231B146F746323031F9EF5935988A2FD97551A327EC9B5697D` |
| 源码摘要 | `E4ECB950FCD2A3A92D5421B4E2AF70252BC9C72A6F98B21E24F860FD287C72C6`，与既有构建凭据一致 |
| 固件映像 | SHA-256：`90C3BB80E8CE4AFFCC25517DC072DE7A796C6970EFC683C03A3FD02DA6CD78A1`；OpenOCD 校验板上映像与当前 ELF 一致，实际验证 355780 字节 |

原理图目录实际修正为 `E:\MCU\MCU_Save\STM32\STM32F407ZGT6-ARM-Cortex-M4\原理图`；本次参考 `EXPLORER_V3.4.pdf` 和 `EXPLORER_V3.5.pdf`，对应渲染页保留在 `artifacts/validation/f407-rtos-hardware-20260926/schematic/`。原理图核对不能替代本次实际探针及板上映像校验。

## 实际操作与读取结果

主调试代理运行 `tools/StudioX.RtosHardwareValidation` 的显式硬件模式，使用隔离用户数据，通过真实 MCP 握手发现 `debug_rtos_snapshot`。本次只批准绑定工程的硬件附加和 continue/pause/stop，没有编译、修改用户工程、下载、擦除、复位、写内存或创建断点，也没有调用目标端内核统计函数。

附加暂停后，`hardware=true`、`simulated=false`，板上固件校验通过。读取第一份快照，继续约 200ms，发出暂停并用 `debug_wait` 等到 `Stopped`，读取第二份快照；随后结束会话。暂停期间界面的 `Running` 只标记内核选择的当前任务，不表示暂停后的 CPU 仍在执行任务指令。

两份快照均报告调度器已启动、挂起嵌套次数为 0、任务数为 3：

| 任务 | 状态 | 当前优先级 | 基础优先级 |
| --- | --- | --- | --- |
| IDLE | Running | 0 | 0 |
| lvgl | Blocked | 2 | 2 |
| Tmr Svc | Blocked | 2 | 2 |

`Tmr Svc` 的等待状态读取为 Blocked，未把无限期等待误报为显式挂起。两次暂停停在空闲任务，第二次源码位置为 `device/sdk/freertos/tasks.c` 的 `prvIdleTask`；这不是持续调度轨迹或所有任务切换的验收。

| `heap_4` 统计 | 实际值 |
| --- | --- |
| 总容量 | 32768 字节 |
| 当前空闲 | 22520 字节 |
| 历史最低空闲 | 22520 字节 |
| 最大空闲块 | 22520 字节 |
| 空闲块数 | 1 |
| 成功分配 / 释放次数 | 7 / 0 |

注册表发现 `TmrQ`，计数为 0、容量为 10、单项大小为 12 字节；等待发送任务数为 0，等待接收任务数为 1。结果依据注册对象和目标内存读取，未注册对象不由此判定不存在。

Tick 从 `1292553` 增至 `1292772`，本次观测增加 219。此差异证实两次暂停之间计数实际变化；200ms 是辅助程序的等待请求，不是实时调度或计时精度验收，也不据此推算 CPU 使用率。

## 未提供字段与未验证范围

本次 TCB 没有 `pxEndOfStack`，因此每个任务的 `stackSizeBytes` 和 `stackHighWaterBytes` 都保留 `null`；运行时间计数字段未提供，`runtimeCounter` 也为 `null`，不显示为零或伪造 CPU 占比。内核的 `ucQueueType` 字段未保留，快照保留该诊断；`TmrQ` 依据可读队列数据识别，不能据此确认其它同步对象的细分种类。非互斥对象的 `mutexOwnerAddress` / `recursionCount` 为 `null`。

本次没有验证互斥量、递归互斥量、二值/计数信号量、优先级继承变化、队列集合、软件定时器对象枚举、事件组、消息/流缓冲区、栈水位、堆碎片变化、死锁、多核内核、其它内核版本或其它器件/探针。已通过的软件与静态 ELF 离线检查仍分别记为离线证据。

## 退出、恢复与证据位置

`debug_control stop` 后应用状态为 Disconnected；本次恢复运行的证据另外来自原始会话日志的实际 `OpenOCD < STUDIOX_DETACHED_RUNNING` 输出，不能仅依据 Disconnected，也不能把初始化脚本中的 `echo` 命令当成恢复证明。辅助程序随后取得并释放探针租约；主调试代理另行核对没有残余 GDB/OpenOCD 进程。

本次结果为 `success=true`、`hardwareSessionVerified=true`、`targetRestoredRunning=true`、`probeLeaseAvailableAfterStop=true`，`errors=[]`。原始证据保留在本机验证目录，不包含于源码提交的默认范围：

- MCP 完整结果：[result.json](../artifacts/validation/f407-rtos-hardware-20260926/mcp/result.json)。
- 只读准备及映像/源码凭据：[plan.json](../artifacts/validation/f407-rtos-hardware-20260926/mcp/plan.json)。
- 辅助程序有界并过滤变量/凭据行的调试输出：[debug-trace.log](../artifacts/validation/f407-rtos-hardware-20260926/mcp/debug-trace.log)，本次没有遗漏行。
- 原始 OpenOCD/GDB 会话日志：用户工程 `.build/download-6125cef43e034b16a1d9a6c04c34786c/debug-session.log`；第 72 行报告 `verified 355780 bytes`，第 767 行为实际恢复标记。完整日志仍位于原工程，没有复制或发布。

本记录仅整理主调试代理已完成的实板结果；记录阶段没有再次连接探针、扩大测试、修改版本或制作安装包。
