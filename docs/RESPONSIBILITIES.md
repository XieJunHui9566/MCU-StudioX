# 工作台职责导航

## 从入口寻找实现

| 需求 | 入口与实际职责 |
|---|---|
| 服务创建与关闭 | `Application/WorkbenchService.cs` 是组合根，各服务管理自身资源 |
| 工程切换 | `Desktop/MainWindow.Project.cs` 处理界面，`ProjectTransitionCoordinator.cs` 固定旧会话的退出顺序 |
| AI 会话与草稿 | `Desktop/AiConversationController.cs` 管理历史、草稿、工程代次和持久化结果 |
| AI 工具生命周期 | `Desktop/AiMcpSessionCoordinator.cs` 等待调用结束，清理旧目录授权和设备会话 |
| AI 活动和授权 | `AiActivityPresenter`、`AiApprovalPresenter`、`AiTranscriptRenderer`、`AiTurnDetailRenderer` 分别呈现进度、临时审批、气泡和历史详情 |
| 上下文和实际缓存量 | `Desktop/AiUsageMeterPresenter.cs` 展示 API 返回字段，缺失值保留未知 |
| 实时文件刷新 | `Desktop/EditorDocumentSynchronizer.cs` 是磁盘内容应用入口；主窗口同步标签、语言服务和界面视图 |
| 模型协议 | `Application/AiChatClient.cs` 管 HTTP 和凭据；请求编码、响应解析、SSE 与工具增量分别有独立实现 |
| Agent 循环 | `Application/AiAgentService.cs` 调度模型与 MCP，`AiAgentService.Context.cs` 管消息保留与回收；公开协议类型分文件 |
| MCP 工具发现 | `Application/Mcp/StudioXMcpTools.cs` 组合 16 个工具组、排序注册和清理，不实现编程、调试或文件复制 |
| 工程文件与外部目录 | `WorkspaceMcpTools` 调用工程文件服务；外部授权、路径策略、游标浏览与复制事务各自独立 |
| 编译和 Git | `BuildMcpTools`、`GitMcpTools` 调用对应应用/引擎服务 |
| 调试和 RTOS | `DebugMcpTools` 调用 `DebugSessionService`；Engine 的 MI、反汇编与 FreeRTOS 检查独立于 WPF |
| 模拟设备和采集 | `Application/Simulation/SimulationLabService.cs` 拥有会话、两路订阅和记录器；Desktop 只显示事件 |
| LVGL PC 预览 | Engine 扫描库、构建和启动原生窗口，Application 管预览会话/资源，Desktop 提供配置与状态 |

MCP 工具组完整目录见 `src/StudioX.Application/Mcp/README.md`；代码和注释约定见 [CODE_STYLE.md](CODE_STYLE.md)。

## 所有权与退出顺序

工程打开前先验证目标清单、确认当前脏文档。切换和关闭共用以下顺序：结束 Agent/MCP → 保存断点 → 结束 PC 预览 → 结束调试及迟到导航 → 结束编辑辅助和语言服务。所有旧请求结束后，窗口再关闭旧文档、清理界面并绑定新工程。清理失败保留原始异常，不擅自绑定新工程。

AI 控制器、用量呈现、协议解析和路径策略独立于主窗口状态。WPF 主窗口仍负责工作台控件之间的呈现协调；它的事件处理器调用这些组件，不接管组件的业务状态。后续功能应在对应组件中扩展，避免继续向主窗口追加连接、协议或事务实现。

编辑器同步保留脏缓冲区的原磁盘基线，避免把外部修改覆盖掉；干净缓冲区应用磁盘文本后清除旧撤销基线，并恢复光标、选区与滚动。每次 AI 文件操作完成和整轮结束检查使用同一个应用入口。

模拟设备由 Application 单独拥有，日志、绘图和记录器各自订阅同一连接。慢观察者使用有界缓冲并报告丢帧；退出先使旧代次失效，再刷新记录文件、结束观察者和释放连接。这里的时间戳和统计始终标为模拟/宿主来源。

## 本次门禁

- 构建整个解决方案，保留零警告要求。
- 比较完整 MCP 工具 JSON 契约，包含名称、描述和参数 schema；仅计数相同不算兼容。
- AI 离线验证覆盖缓存、提示注入、SSE、图像、工具增量及超过 64 个工具定义的请求。
- 桌面控制器验证对话隔离、草稿、审批卡消失、API 用量缺失值与脏缓冲区。
- 文档预览验证连续文件写入后的即时刷新，以及光标、选区和滚动。
- 工程生命周期验证旧 Agent 等待、退出顺序、取消与错误传递。
- LVGL 使用隔离自定义 UI 夹具验证真实 PC 构建和窗口，不连接开发板。
