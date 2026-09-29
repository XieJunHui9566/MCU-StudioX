# Agent 编辑与授权验收

日期：2026-09-29。产品版本保持 0.2.5.2，本轮为本地功能开发，没有安装、发布、Git 提交或硬件操作。

## 交付范围

- 聊天栏「逐次确认 / 全面授权 / 完全访问」，按工程保存在用户数据目录；选择后直接生效，重启恢复。
- 编辑器上下文、未保存正文读取与搜索；C/C++ 定义、引用、诊断、语义重命名、格式化与诊断修复接入 Agent 工具。
- 跨文件计划预览、逐块应用、单计划及整任务撤销。语义重命名在核心服务中强制整体应用。
- 编译前保存当前快照；实际构建日志、产物与验证状态关联任务。代码变化后使旧验证结果过期。

## 自动检查

全部 394 项检查通过，另执行原生鼠标和键盘交互。模型传输使用确定性脚本，MCP、clangd、CMake 和 GCC 为真实本机服务；结果不代表在线模型质量或硬件验收。

| 检查 | 结果 | 主要证据 |
|---|---|---|
| Agent 工作流与边界 | 67 项 | `artifacts/validation/agent-workspace-verified/result.txt`、`artifacts/agent-workspace-verified.log` |
| WPF 编辑器与授权入口 | 22 项 | `artifacts/validation/agent-desktop-verified/result.txt` |
| AI 传输、流式状态与运行中提示 | 41 项 | `artifacts/agent-regression-StudioX.AiValidation.log` |
| 工程切换与资源归属 | 13 项 | `artifacts/agent-regression-StudioX.ArchitectureChecks.log` |
| 桌面职责、审批与会话 | 61 项 | `artifacts/agent-regression-StudioX.DesktopArchitectureChecks.log` |
| MCP 工具与协议 | 150 项 | `artifacts/agent-regression-StudioX.McpValidation.log` |
| 搜索、跨文件编辑、真实 clangd 与草稿恢复 | 40 项 | `artifacts/agent-editor-regression.log` |

`tools/Build.ps1 -BuildArtifactsDirectory artifacts/build/agent-upgrade` 成功，0 个警告、0 个错误，日志为 `artifacts/agent-final-build.log`。

核心场景使用脚本模型调用真实 MCP：读取源码，生成并应用缺失分号的计划，首次 GCC 编译失败，使用 clangd 的诊断修复生成计划，再次编译成功，实际生成 ELF/BIN/HEX/MAP。全面授权下不新增编辑、保存或编译确认。跨文件重命名通过三个文件验证，并保留无关的同名局部变量。

冲突测试覆盖用户在预览后编辑、第二个文件被外部程序改写、保存期间变化、取消保存、受保护路径、部分语义重命名，以及撤销区域发生后续编辑。全任务撤销先在内存逆序还原全部计划，再一次校验应用，任一冲突都保留当前文件。

完全访问测试仅验证授权分类与桌面审批路由；没有执行串口、下载、调试连接或 Git 远端调用。外部 MCP 未增加编辑器工具。回归中发现既有协议基线遗漏了先前实现的 6 个 AG32 工具，以及一个扩展型号的描述；逐项核对后更新基线和工具组数量检查，未取消协议一致性检查。

## 原生交互

通过 Windows 原生鼠标和键盘操作隔离工程：取消第二个修改块，只应用初值变更；打开源码确认返回值未被修改；手工追加注释后撤销整个任务，初值恢复且注释保留；保存源码，选择全面授权，关闭并重启测试进程，确认模式恢复。

截图位于 `artifacts/validation/agent-native/`：`undo-preserves-manual.jpg`、`full-authorization.jpg`、`restored-mode.jpg`。WPF 明暗主题与 1100×760 窄窗口截图位于 `artifacts/validation/agent-desktop-verified/`。窄窗口的计划列表改为顶部排列，修改详情可以滚动查看。

## 当前边界

预览和任务撤销覆盖 `editor_*` 工具的修改计划；传统磁盘文件工具及复制保留原有操作记录。计划随工程编辑器会话结束，草稿与本地历史继续保留。Python 已支持正文读取、搜索和文本修改计划；语义查询、重构及诊断暂为 C/C++。外部 stdio MCP 仍逐次授权，桌面工程的模式不影响外部客户端或插件面板。

尚未使用真实在线模型账户验收，也没有实板、固件下载或远端仓库验收。
