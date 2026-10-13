# AG32VF303CCT6 实机 IDE 调试验收（2026-09-23）

## 环境

- 目标：已供电的 AG32VF303CCT6 开发板，官方 AGM DAP-LINK CMSIS-DAP v2，SWD 1 MHz。
- 探针 USB 经 Genesys Logic `VID_05E3&PID_0610` 扩展坞接入电脑；本次扩展坞未接外部电源。此前的[通信排查](AG32-DEBUG-INVESTIGATION-20260923.md)已确认电脑 USB 直连在持续读取时会失去探针响应。
- IDE：首轮使用 `artifacts/MCUStudioX-AG32-Debug-20260923-Final/MCU StudioX.exe`，修复后使用 `artifacts/MCUStudioX-AG32-IDE-Debug-20260923-R2/MCU StudioX.exe` 复测。工程：`.artifacts/ag32-final-publish-validation-20260923/minimal`，器件包 `studiox.preview.ag32vf303` 0.1.1。
- 使用工程已有的带 DWARF 调试信息的 ELF。其 1,304 字节加载映像与目标板现有固件一致；IDE 启动时执行 `monitor verify_image`，返回 `verified 1304 bytes`。本次没有下载、擦除或修改选项字节。

## 实机结果

| 操作 | 结果 |
|---|---|
| 启动调试 | OpenOCD 识别 `device id = 0x40200001`、256 KiB Flash；GDB 附加，源码定位到 `src/main.c:23`、`0x800003ac`，IDE 提示“实机固件校验通过”。 |
| 寄存器、调用栈 | `-data-list-register-values x` 成功，寄存器页显示 RV32 值；调用栈显示 `main` 第 23 行。 |
| 变量观察 | `app_heartbeat` 读为 `2754315408`；自由运行并手动暂停后读为 `2909901847`。 |
| 硬件断点 | 在 `src/main.c:23` 设置断点，GDB `-break-insert -h` 返回 `hw breakpoint`。继续与单步进入时分别命中，IDE 到达次数从 0 增至 2。测试后删除断点。 |
| 单步 | 逐过程从第 23 行到第 22 行、`0x800003b2`；单步进入回到第 23 行、`0x800003ac`。 |
| 运行、暂停、内存 | 清除断点后自由运行，随后手动暂停成功。只读读取 `0x20000110` 起始 64 字节，前四字节为 `17 94 71 AD`，按小端序等于暂停后的 `app_heartbeat = 0xAD719417`。 |
| 结束调试 | 日志显示 GDB `-target-detach` 成功、`-gdb-exit` 成功以及 OpenOCD `STUDIOX_DETACHED_RUNNING`；IDE 恢复工程与构建面板，OpenOCD/GDB 进程已退出。 |

首轮原始协议与进程日志：`.artifacts/ag32-final-publish-validation-20260923/minimal/.build/download-507b2ee833df4033ae5096fc69649895/debug-session.log`。`STUDIOX_DETACHED_RUNNING` 是 OpenOCD 在结束时确认目标已恢复运行的标记；本次没有在脱离后另接仪器测量目标引脚。

## 修复后复测

R2 再次通过 1,304 字节板上固件校验与实机寄存器读取。在不可执行的 `src/main.c:14` 请求断点时，厂商 GDB 返回实际绑定行 18；IDE 边栏与断点列表显示第 18 行，从实际行点击可删除该断点。第 24 行注释上的断点被 GDB 标为 `<PENDING>`，IDE 显示“未绑定”，没有伪装成已生效。

清除断点后，R2 的顶部与底部状态栏在自由运行时都显示“运行中”，手动暂停后都显示“用户暂停”。结束调试时再次获得 GDB `-target-detach` 与 OpenOCD `STUDIOX_DETACHED_RUNNING`，OpenOCD/GDB 进程退出，寄存器和调试页签收起。复测原始日志：`.artifacts/ag32-final-publish-validation-20260923/minimal/.build/download-cc548ab259034837823d49f67354ca8b/debug-session.log`。

这个较早创建的测试工程仍保存了 F407 示例观察项 `app_counter`、`app_input`、`app_output`，它们在 AG32 上显示“无此符号”。新建 AG32 工程的默认观察项已修为 `$pc`；旧工程的用户观察项按工程配置保留。该默认值通过离线 AG32 验证，未在此次旧工程复测中重新生成工程。

本次验证覆盖当前这一块板、官方探针及上述扩展坞 USB 路径；电脑 USB 直连的间歇性通信故障仍按[通信排查](AG32-DEBUG-INVESTIGATION-20260923.md)处理。
