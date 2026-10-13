# CH32V307 FreeRTOS 实板调试验收（2026-09-26）

本次在 CH32V307VCT6 / WCH-LinkE 上完成明确授权的试验固件下载、板上映像校验及 FreeRTOS V10.4.6 的只读 MCP 调试。用户允许写入该试验程序并明确无需考虑原固件；结束后保留试验程序运行。此次结果不扩大到 RCT6/WCU6 或其它内核版本。

## 固件与连接

| 项目 | 配置或证据 |
| --- | --- |
| 工程 | `artifacts/validation/ch32v307-rtos-hardware-20260926/firmware`，由 `wch.ch32v307/0.1.3` 的 `spl-freertos` 模板生成的隔离验收工程 |
| 器件与探针 | CH32V307VCT6，WCH-LinkE，日志报告 RV 固件 2.16；SDI 6000kHz，未指定序列号 |
| 内核与工具 | FreeRTOS V10.4.6，`wch.riscv/1.0.0`；复用已有工具运行时，没有复制开发环境组件 |
| 映像 | BIN 7984 字节；SHA-256 `407286CB1791CA1F78E8EDC46526A9CD6EA316C36C345FFF672B53E2A00FBF6D` |
| ELF | SHA-256 `CAF88D93C719ED22B50AF98FB169DFC5D4AD7A55A7447A7E6DE9DD83EED2E51B`；可加载文件字节共 7984 |
| 源码摘要 | `5B0E475A2B63015DE80C4ABB8F95B5059F065A6369EA09C5C8256082EDB33C9D` |
| 下载与读回 | 下载日志报告按页写入 8192 字节，实际校验 7984 字节；随后调试附加再次报告 `verified 7984 bytes`，没有实际差异 |

7984 是映像及校验字节数，不是精确擦写范围。此验收固件添加了观察配置及五种对象；其 Flash 7984 字节、RAM 15492 字节与原包模板的离线占用分别记录，没有修改或重新制作器件包。

## 校验误判与修复

最初附加另一份未匹配的 ELF 时，WCH OpenOCD 输出实际字节差异，MI 却仍返回 `^done`。当时应用和辅助程序错误地接受连接，原 `initial/result.json` 的 `success=true` 是误判：任务数和堆统计读成异常数值，不是成功验收。该结果保留为缺陷证据，见 [initial/assessment.json](../artifacts/validation/ch32v307-rtos-hardware-20260926/initial/assessment.json)。

修复后，下载与附加共用实际输出校验：必须收到正向 `verified`，字节数与映像完整可加载字节数一致，并拒绝实际差异或传输失败；单独的 MI `done` 不作为依据。CRC 警告后若完整逐字节比较通过且无实际差异，可以通过。内核读取拒绝严重无效的核心数据，验收辅助程序另检查任务数、TCB、调度器及堆统计的一致性。

修复后的 [mismatch-guard/result.json](../artifacts/validation/ch32v307-rtos-hardware-20260926/mismatch-guard/result.json) 正确返回 `DEBUG_IMAGE_MISMATCH`，没有进入 RTOS 读取，且恢复运行、释放探针。随后才下载用户授权的上述试验映像并执行以下验收。

## 两次暂停快照

真实 MCP 会话发现并调用 `debug_rtos_snapshot`。附加时校验映像，暂停后读取；继续约 200ms，再暂停并等待 `Stopped` 后复读，最后结束会话。附加部分没有下载、复位、写内存或新增断点，也没有调用目标端统计函数。此前独立的授权下载步骤包含映像写入、校验及复位运行。

两份快照均为 `hardware=true`、`simulated=false`，内核版本 V10.4.6、调度器已启动、挂起嵌套数 0、任务数 3：

| 任务 | 状态 | 当前 / 基础优先级 | 记录的栈容量 | 未使用栈填充区 |
| --- | --- | --- | --- | --- |
| IDLE | Running | 0 / 0 | 500 字节 | 360 字节 |
| task200 | Blocked | 2 / 2 | 500 字节 | 344 字节 |
| task500 | Blocked | 2 / 2 | 500 字节 | 344 字节 |

`Running` 表示调度器选择的当前 TCB，不表示暂停的 CPU 仍执行任务。500 字节来自 TCB 记录的边界，受端口对齐影响，不能当作实际分配大小。运行时间计数不可读，保留 `null`，不推算 CPU 占比。

`heap_4` 两次均为：总容量 12288 字节，当前空闲、历史最低空闲和最大空闲块均为 9856 字节，空闲块 1，成功分配 / 释放次数为 11 / 0。

| 注册对象 | 类型 | 当前计数 / 容量 | 单项字节 | 发送 / 接收等待任务 |
| --- | --- | --- | --- | --- |
| validation_queue | Queue | 2 / 4 | 4 | 0 / 0 |
| validation_binary | BinarySemaphore | 1 / 1 | 0 | 0 / 0 |
| validation_counting | CountingSemaphore | 3 / 5 | 0 | 0 / 0 |
| validation_mutex | Mutex | 1 / 1 | 0 | 0 / 0 |
| validation_recursive | RecursiveMutex | 1 / 1 | 0 | 0 / 0 |

两种互斥量均未持有，持有者地址和递归次数为 0；非互斥对象对应字段保留 `null`。Tick 从 `11711` 增至 `11806`，本次增加 95；这证明两次暂停之间计数变化，不是调度时序、200ms 精度或 CPU 使用率的验收。

## 退出、范围与证据

原始会话日志在 `23:48:35.943` 收到实际 `OpenOCD < STUDIOX_DETACHED_RUNNING`，并确认探针租约可重新取得；主调试代理另核对无残余 OpenOCD/GDB。结果为 `success=true`、`hardwareSessionVerified=true`、`targetRestoredRunning=true`、`probeLeaseAvailableAfterStop=true`，`errors=[]`；独立最终核对 12 项全部通过。

本次产品构建为 0 警告 / 0 错误；内核离线检查 25 项、映像输出回归 12 项、辅助程序自测 8 项，共 45 项通过，分别作为软件证据。未实板验证持有互斥量、递归加锁、优先级继承、同步对象争用、堆碎片变化、死锁、SMP、其它芯片或其它内核版本。

本机证据保留在以下位置；原始日志不随默认源码提交发布：

- [下载结果与逐次授权](../artifacts/validation/ch32v307-rtos-hardware-20260926/download/download-result.json)、[MCP 两份快照与退出结果](../artifacts/validation/ch32v307-rtos-hardware-20260926/mcp/result.json)、[最终独立核对](../artifacts/validation/ch32v307-rtos-hardware-20260926/hardware-acceptance.json)。
- [源码及映像准备凭据](../artifacts/validation/ch32v307-rtos-hardware-20260926/mcp/plan.json)、[固件离线编译摘要](../artifacts/validation/ch32v307-rtos-hardware-20260926/firmware-offline-summary.json)。
- 原始下载日志：[openocd.log](../artifacts/validation/ch32v307-rtos-hardware-20260926/firmware/.build/download-31a5f6ba3c5f405cba9a57c638cda8b2/openocd.log)，第 14 行报告探针版本，第 28/30 行报告写入及校验字节。
- 原始附加日志：[debug-session.log](../artifacts/validation/ch32v307-rtos-hardware-20260926/firmware/.build/download-c283a27ce39146479741b7140294291c/debug-session.log)，第 78 行为完整映像校验，第 1046 行为实际恢复标记。

本轮验证目录约 31.02 MiB、217 个文件；没有复制开发环境组件。本记录仅整理已完成的结果，记录阶段没有再次连接硬件、更新版本或制作安装包。
