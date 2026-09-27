# MCP 职责与维护入口

`StudioXMcpTools` 仅负责组合、注册和关闭工具组。内置 Agent 和外部 stdio 主机使用相同注册结果；名称按序排列，参数、描述和错误结构属于客户端契约。

## 业务工具组

| 工具组 | 负责内容 | 状态所有者 |
| --- | --- | --- |
| `WorkspaceMcpTools` | 工程信息、源码列表、搜索、读取、局部补丁和新建 | 单次文件操作 |
| `BuildMcpTools` | 已授权编译、读取原始日志 | 构建服务 |
| `GitMcpTools` | 当前工程的 Git 查询与写入 | Git 服务 |
| `ExternalProjectMcpTools` | 外部目录入口、分页浏览、定位、读取、搜索 | 浏览游标 |
| `ExternalProjectCopyMcpTools` | 外部库复制的清单审批、暂存与提交 | 单次复制事务 |
| `DebugMcpTools` | 调试控制、寄存器、内存、反汇编、FreeRTOS 快照 | 本 MCP 会话启动的调试会话 |
| `SerialPlotMcpTools` | 串口收发、原始数据、绘图采集 | 本 MCP 会话创建的串口/绘图实例 |
| `FirmwareDownloadMcpTools` / `StcIspMcpTools` | 下载规划与逐次授权烧录 | 下载服务 |
| `DeviceCatalogMcpTools` / `MicrochipMcpTools` | 本机器件包与厂商资料 | 查询服务 |
| `LvglMcpTools` | UI 配置、PC 预览、输入、截图与资源报告 | LVGL 预览服务 |
| `SkillMcpTools` | 技能元数据、说明和参考文件 | 技能服务 |
| `PdfMcpTools` | PDF 列表、文字、搜索、页面图像 | 单次 PDF 读取 |
| `QmdMcpTools` | 绑定工程的 BM25 快照与索引 | 工程有界缓存 |
| `WebMcpTools` | 公开网页搜索与提取 | 联网服务 |

## 共享边界

- `McpSessionContext` 保存工程身份、宿主审批入口及应用服务，不持有业务设备或浏览游标。
- `McpWorkspaceAccess` 校验绑定工程；`McpWorkspacePathPolicy` 统一源码读写路径规则。
- `ExternalProjectAccess` 保存当前会话的外部只读授权；`ExternalProjectPathPolicy` 为浏览、复制、PDF 和 LVGL 提供相同的路径、链接与敏感文件过滤。
- `McpDebugAccess` 为下载和调试提供一致的工程占用检查。
- `StudioXMcpDiagnosticFunction` 仅将有明确错误码的业务错误转换为可展示诊断，隐藏凭据；未知异常保留 SDK 的处理方式。

新增工具优先加入现有职责明确的工具组，新增独立业务时创建新组，并在注册入口显式组合。工具不能绕过共享路径校验，也不能借技能或外部资料扩大权限。

## 生命周期

设备会话与浏览游标由各工具组关闭；注册入口逐组清理，即使某组失败也继续清理其他组，最后汇总原始异常。调试只停止本 MCP 会话启动的会话，IDE 自建会话不归 MCP 所有。所有外部目录授权在会话结束时清除。

`StudioX.McpValidation --contracts <文件>` 可记录完整协议契约，`--verify-contracts <基线文件>` 核对实际注册结果的名称、参数和描述。职责拆分前后的记录应一致；完整验证另外覆盖写入授权、外部复制、分页、只读边界、离线调试及内置/外部客户端。

完整验证默认核对随验证程序复制的 `Fixtures/mcp-tool-contracts.json`，不依赖本机 `artifacts` 或当前工作目录。主动改变协议时应审阅并更新这一基线，不能在回归时自动覆盖它。
