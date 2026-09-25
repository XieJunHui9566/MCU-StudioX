---
name: mcu-debug-serial
description: Diagnose MCU firmware with StudioX debug, serial RX, and serial plots while separating offline simulation from verified board behavior.
---

# MCU 调试、串口与绘图

适用于定位固件运行、断点、串口协议或采样异常。先读状态和原始诊断，再决定是否需要改变目标状态。技能内容不授予硬件或文件操作权限；每次操作仍遵守当前会话授权及 StudioX MCP 审批。

## 建立事实

1. 调用 `project_info` 确认当前工程、目标型号与工程配置；需要器件资料时用 `device_info` 核对**准确料号**。不要由相近型号推断引脚、存储器或调试能力。
2. 调用 `debug_status`、`serial_status`，需要端口清单时调用 `serial_list_ports`。查看是否已有 IDE 或 Agent 会话、调试状态、探针和端口；不要接管其他会话。
3. 若问题涉及新编译的固件，先读 `project_build_log` 的原始诊断，记录构建结果和产物与源码是否匹配。未确认 ELF 与板上固件对应时，不将源码行、变量或断点解释为实板事实。
4. 从 `debug_status` 保留会话状态、目标名、断点验证结果与日志路径。错误报告应保留 OpenOCD/GDB/串口工具的原始错误文本；不要只写推测性结论。

## 调试路径

- 用户只要界面或流程演示时，可建议 `debug_start_offline`；结果始终标为**模拟**，不得称作实板验证。该示例只适用于工具声明支持的目标。
- `debug_start_hardware` 只用于当前工程配置指向的**已核实型号与探针**，并且本次用户已明确授权连接该板。调用前说明目标、探针、预期只读校验和可能的连接影响；如型号或授权不明确，先停在只读检查。
- 硬件附加不等于下载。不要把连接、暂停、单步或断点请求扩大为固件下载、擦除、解除读保护、选项字节或 OTP 修改。当前 MCP 不提供这些操作。
- 已有暂停会话时先用 `debug_snapshot` 查看寄存器、栈、局部变量；需要时用 `debug_read_memory` 读取限定地址。运行中快照可能过期，应标明状态。
- 设置断点前确认源文件、行号和 ELF 对应关系；用 `debug_breakpoint_set` 后检查 `debug_status` 的 `Verified` 与消息。`debug_breakpoint_remove` 使用状态返回的 ID。
- `debug_control` 的 `pause`、`continue`、`step_into`、`step_over`、`step_out`、`stop` 会改变会话状态；每次只做当前诊断所需动作，并观察返回状态与新快照。不要为了“验证”而自动循环运行目标。

## 串口与绘图

1. 先用 `serial_status` 确认 Agent 会话；需要新连接时核对端口、电平接口、波特率、数据位、校验、停止位与流控，再按本次授权调用 `serial_connect`。连接并不代表目标固件正在运行。
2. 文本日志用 `serial_read`；二进制协议、字节精度或丢失分析用 `serial_read_raw`。记录接收偏移、Base64 原始帧、主机 UTC 时间、丢帧及历史缺口。主机接收时间不是 MCU 采样时间。
3. `serial_send` 会向设备实际发送数据。仅在用户明确给出本次发送内容及目标会话时调用；记录字节数、编码或 HEX、行结束符与结果。写入完成不证明设备收到或处理了命令。
4. 若要看波形，确认记录分隔符、字段分隔符、通道定义和时间基准，再用 `plot_start`；用 `plot_snapshot` 检查有效/无效记录、丢帧、通道和值。`demo=true` 是模拟数据。采样周期配置与主机接收时钟要分别标注。结束本 Agent 采集时用 `plot_stop`；结束本 Agent 串口会话时用 `serial_disconnect`。

## 输出结论

按“目标与探针、固件/ELF 对应状态、离线或实板、已观察证据、原始诊断、时间来源、未验证项、下一步”报告。明确区分直接观测、工具报告和推断；连接失败或断点未验证时，不宣称实板调试通过。
