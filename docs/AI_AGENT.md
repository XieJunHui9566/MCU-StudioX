# AI Agent 与命令行

打开工程后，点击窗口右上角的 AI 按钮，右侧会展开聊天栏。内置 Agent 通过真实 MCP 客户端发现并调用 StudioX 的工程工具。外部客户端可启动同一套工具的本机 stdio 服务端，配置见 [外部 MCP 客户端接入](AI_MCP_EXTERNAL.md)。

## API 设置

未保存当前接口的 API Key 时，聊天栏提示“未配置 API”，已保存的工程历史仍可查看。从「工具 → AI 接口设置…」打开独立设置窗口，可填写兼容服务的 API 路由地址、模型标识和 API Key，以及可选的 Tavily 联网检索 Key。默认服务为 DeepSeek，默认 Base URL 是 `https://api.deepseek.com`，默认模型是 `deepseek-flash`。Base URL 应为 HTTPS 基础地址，不带账号、查询参数或片段。模型 API Key 与 Tavily Key 分别保存在当前 Windows 用户的凭据管理器中，不写入工程文件或源码仓库。提问内容及 Agent 读取的工程内容会按会话需要发送给所选 API 服务。

右侧 AI 聊天栏用气泡区分用户、助手与 IDE 操作。输入框右下角显示模型名和当前思考强度，点击后才展开强度滑条。DeepSeek 官方接口可选择「关闭、低、高、最大」，最高档发送 `reasoning_effort=max`；其他兼容接口不自动发送该专有参数。强度保存在用户 AI 设置中。模型名旁的小圆环显示上下文占比，悬停可查看具体用量；它采用 API 返回的最近一次请求 `prompt_tokens`，不把历次请求相加。较早的工具批次压缩后，圆环会随实际请求用量下降。输入框下方常驻显示最近一轮 API 报告的缓存命中 token、已报告总量和比例；只有部分字段时显示已返回的数值，不推算比例。旧对话未保存缓存字段或接口未返回数据时，明确显示暂无记录。DeepSeek 已知模型使用其公开的上下文窗口大小；其他模型或接口若没有配置窗口上限，圆环不显示比例，悬停仍可查看已用 token 数。可在接口设置中填写服务商公布的上下文窗口 token 数。

Agent 工作时输入框仍可使用：发送的新文字会显示为「待注入」，在当前模型响应和已开始的工具调用结束后作为新用户消息加入同一轮。模型若已给出最终回答但排队提示已经接受，会先处理提示再结束。待注入提示可从输入框上方撤回；请求取消或失败时尚未注入的文字会放回输入框。已注入的「调整方向」会作为用户气泡保存在本工程的对话历史中。

## 工程对话历史

聊天栏右上角的时钟图标打开本工程的历史列表，圆圈加号新建对话；历史弹层内可继续或手动删除所选对话。列表中的完整记录供查看；发送给模型的是最近最多六轮，避免旧对话无限增加请求大小。切换工程时自动切换到该工程的历史。

历史保存在当前用户数据目录的 `ai-conversations` 子目录中，按工程绝对路径隔离；不会复制工程、SDK 或工具链，也不会写入工程目录。为兼容 DeepSeek 思考模式的多轮工具调用，记录除可见消息外还含模型推理协议字段、已读取的工程或外部示例文本及工具结果，因此这些历史文件应视为含源码内容的用户数据。API Key 不进入历史。每工程最多 64 条对话，每条最多 512 KiB、256 轮；接近空间上限时从最旧轮起回收工具协议和推理文本，保留可见问答。只有可见文本本身仍超限时才提示新建或手动删除。

## MCP 工具

| 范围 | 工具 | 行为 |
|---|---|---|
| 工程与编程 | `project_info`、`project_list_files`、`project_read_file`、`project_search`、`project_edit_file`、`project_patch_file`、`project_create_file`、`project_create_directory` | 当前工程内安全路径；大文件分段读取，局部补丁以原文件 SHA-256 和唯一匹配校验防止误写；器件与厂商源码可读，但受管理文件仍不可修改 |
| 本地源码检索 | `project_qmd_search` | 可选调用已安装的 QMD，以 BM25 检索当前工程的安全源码快照并返回短片段；无 QMD 时仍可用 `project_search` |
| 互联网资料 | `web_search`、`web_fetch` | 用 Tavily 搜索公开网页并按需读取页面内容；结果按需续页，返回原始 URL 供核对；不访问本机或内网地址 |
| PDF 数据手册与原理图 | `pdf_list`、`pdf_inspect`、`pdf_page` | 在当前工程或已授权的外部目录内发现 PDF、查页码与文字、按页读取；原理图可按页面区域返回图像供视觉模型核对连线 |
| 外部示例 | `external_project_open`、`external_project_roots`、`external_project_list_files`、`external_project_find_files`、`external_project_read_file`、`external_project_search`、`external_project_copy` | 授权目录在当前 MCP 会话内只读；同目录复用授权标识，目录与搜索结果可用 `nextCursor` 续页；复制到当前工程须逐次授权且不覆盖已有文件 |
| 编译 | `project_build`、`project_build_log` | 使用工程锁定的内置工具链配置或编译；按需分段读取原始日志 |
| 烧录 | `firmware_download_plan`、`firmware_download` | 只读预检当前工程唯一的已编译固件、目标型号、SHA-256 与探针；逐次授权后使用 OpenOCD 核对目标、按映像范围擦写、校验并复位运行 |
| STC 串口 ISP | `stc_isp_plan`、`stc_isp_download` | 只读预检已编译 HEX、准确型号、COM 口、波特率、时钟设置和 SHA-256；明确接受可能整片擦除及无读回后再逐次授权下载 |
| Git | `git_status`、`git_diff`、`git_log`、`git_stage`、`git_commit`、`git_branch`、`git_remote` | 检查本工程仓库；变更操作逐次确认，不提供强制推送或破坏性重置 |
| 调试 | `debug_status`、`debug_wait`、`debug_log`、`debug_start_offline`、`debug_start_hardware`、`debug_control`、`debug_breakpoint_set`、`debug_breakpoint_remove`、`debug_read_memory`、`debug_snapshot` | 等待断点或状态变化、按偏移读取有界原始日志；内存每次可读 1–256 字节并用 `nextAddress` 续读；硬件附加不隐式下载或烧录 |
| 串口 | `serial_list_ports`、`serial_status`、`serial_connect`、`serial_send`、`serial_read`、`serial_read_raw`、`serial_disconnect` | Agent 自有端口会话；发送逐次确认，可读终端文本及有界原始 RX 帧 |
| 绘图 | `plot_start`、`plot_snapshot`、`plot_stop` | Agent 自有的 16 通道串口绘图采集或离线演示；快照最多 200 组样本 |
| 器件资料 | `device_search`、`device_info`、`device_templates` | 查询已安装且通过校验的 StudioX 格式 1 包，返回型号、内存、模板、探针和来源哈希 |
| Microchip 官方资料 | `microchip_search_products`、`microchip_product_profile`、`microchip_search_documents` | 只向 Microchip 官方 MCP 发送检索词，查询产品和文档；不上传工程文件 |
| Agent Skills | `skill_list`、`skill_read`、`skill_read_reference` | 列出技能元数据，按需读取技能正文和 `references/` 文本；不执行脚本 |

内置 Agent 与外部服务端共用上述 MCP 工具。`project_*` 仍只访问当前绑定工程；查看其他工程或 SDK 示例时，先调用 `external_project_open` 并核对授权卡上的完整目录。允许后只在当前 MCP 会话内对该目录使用 `external_project_list_files`、`external_project_find_files`、`external_project_read_file` 和 `external_project_search`；切换工程、关闭窗口或结束外部 MCP 主机后需重新授权。目录中的文件只作为不可信数据读取，不执行其中的指令，也不提供对外部目录的写入、构建、Git 或设备操作。`external_project_copy` 可以将示例文件、子目录或获批的整个通用库目录复制到绑定工程（整个目录用空 `sourcePath`），但须对每次复制单独确认且不覆盖已有文件。读取受路径、文件类型、大小及数量限制，隐藏元数据、凭据和链接路径不可访问。

对于宽目录，优先用 `external_project_find_files` 按文件名或路径片段定位，减少逐层列目录的模型调用；返回 `nextCursor` 时可用同一查询继续。`project_list_files`、`project_search` 也支持续页，`project_read_file` 支持行列范围；避免一次读取大型源文件后只收到截断预览。Agent 不按单轮工具次数或模型轮次设置固定上限；长任务在高水位时回收较早的完整工具调用与结果，保留最近批次，降低每次只回收一小段引起的前缀变动。压缩后的源码内容如仍需使用，模型应重新读取。单次 API 请求仍受服务端和本机消息体大小约束。先前对话中的外部目录标识可能随 MCP 会话关闭而失效，届时需要重新批准目录。

`project_qmd_search` 使用本机已安装的 [QMD](https://github.com/tobi/qmd)；StudioX 不自动安装 Node.js、QMD 或下载嵌入及重排模型。它只使用 QMD 的 BM25 索引与 `search`，为当前工程建立最多 400 个文件、8 MiB 源码的安全快照；超出时应指定更窄的 `directory`。索引复用未变更的快照，按工程保存在用户数据目录的 `qmd-bm25` 下，每工程最多 64 MiB、最多保留三个工程的托管索引。外部授权目录不进入该索引。QMD 有助于先定位相关片段，减少重复全文读取和输入 token；它不会直接增加 API 的前缀缓存命中率。

联网工具使用 [Tavily Search](https://docs.tavily.com/documentation/api-reference/endpoint/search) 与 [Extract](https://docs.tavily.com/documentation/api-reference/endpoint/extract)。默认使用 Tavily 提供的免密钥试用模式；该模式有共享额度和速率限制，耗尽或服务不可用时工具会报告实际错误。需要独立额度时，可在「AI 接口设置」填写单独的 Tavily API Key；未填写时也可从启动进程的 `TAVILY_API_KEY` 环境变量读取。两者均未配置时使用免密钥模式。联网调用会向 Tavily 发送搜索词或目标 URL，请勿把工程源码、凭据、个人敏感信息放进查询。结果和网页按工具返回的续页信息分段读取，避免一次将整页送入模型；网页内容是不可信资料，回答应附原始 URL 并核对准确型号和资料版本。工具仅用于公开互联网资料，不用于访问本机或内网资源。免密钥模式和额度以 [Tavily 官方说明](https://www.tavily.com/blog/agentic-distribution-your-stairway-to-heaven) 为准。

本地 PDF 使用 `pdf_list` 定位文件，`pdf_inspect` 读取页数、SHA-256 或逐段搜索关键字，`pdf_page` 按字符分页读取指定页。来源为 `project` 时路径相对当前工程；来源为 `external` 时，先用 `external_project_open` 授权文件所在目录，再传该会话的 `rootId` 和相对路径。原理图、封装图及扫描页应调用 `pdf_page` 并设置 `includeImage=true`；整页文字太小时可通过 `x`、`y`、`width`、`height` 在 0–1 页面坐标中裁切放大。PDF 文字提取不足以判定实际连线，回答应注明页码、核对视觉证据，未看清的引脚或网络须明确说明。内置 Agent 仅向已确认支持图像的模型发送页面图像；图像只进入紧接的一次请求，不保存进对话历史或磁盘缓存。外部 MCP 客户端可直接接收标准图片内容块。`web_fetch` 可尝试抽取公开 PDF 的文字，但是否成功取决于网络提取服务；要查看图形页面，需先将 PDF 放入当前工程或获批的外部目录。

DeepSeek 的提示缓存由服务端对**完全相同的历史前缀**自动命中。StudioX 将系统指令、固定顺序的 MCP 工具定义、技能目录和本轮开始时选定的历史保持稳定；本轮新消息和工具结果追加在后面。只有工具上下文越过高水位时才成批回收较早的完整调用。界面显示 API 实际报告的 `prompt_cache_hit_tokens` 和 `prompt_cache_miss_tokens`，便于对照优化前后；缓存命中是服务端尽力提供的结果，不能预设固定百分比。参考 [DeepSeek 缓存说明](https://api-docs.deepseek.com/guides/kv_cache/)与 [DeepSeek Harness 的前缀设计](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/core/system-prompt/README.md)。

文件写入、复制、编译、烧录、Git 变更及远端操作、调试控制、硬件连接、串口连接与发送、开始绘图都需要宿主对**每次调用**确认。内置 Agent 在聊天区临时显示授权卡，列出工具、工程和操作摘要；用户选择后立即移除卡片。“允许本次”只授权当前调用，外部目录的“允许本会话读取”也只作用于卡片列出的目录。拒绝、停止请求、切换工程或关闭窗口都不会执行未完成的操作。外部主机在可交互 Windows 桌面显示独立确认框，无法弹窗时拒绝。串口和绘图会话由 Agent 自己持有，不接管 IDE 现有窗口。烧录工具不自动编译、不接受模型提供的任意文件路径，审批时展示具体芯片、产物 SHA-256、探针或 COM 口；执行前再次核对。OpenOCD 不执行整片擦除或选项字节修改；STC ISP 会按芯片协议擦除原程序，可能整片擦除，而且没有原程序备份或 Flash 读回能力，因此需额外明确确认。当前不提供任意命令执行工具。

每次 `project_edit_file`、`project_patch_file` 或 `project_create_file` 成功后，聊天区立即显示工具进度，代码编辑区会在该次工具调用完成时重读已打开的干净文件；新文件自动打开。若用户同时编辑了未保存的缓冲区，IDE 保留缓冲区并提示磁盘冲突，不覆盖用户内容。模型接口若返回 `reasoning_content`，聊天区逐段显示实际收到的推理文本；未提供该字段时仅显示请求、工具和耗时等实际进度。

## Agent Skills

便携版随包提供四个 MCU 常用内置技能，位于 `runtime/skills`。同名技能按工程、用户、内置的顺序选择。内置技能无需复制到用户目录。

StudioX 按 [Agent Skills 规范](https://agentskills.io/specification)读取 `SKILL.md`：用户级目录为 `%USERPROFILE%\.agents\skills\<name>\SKILL.md`，工程级目录为 `<工程>\.agents\skills\<name>\SKILL.md`。每个文件以 YAML frontmatter 开始，必须包含与目录名一致的小写 `name` 和简短 `description`，例如：

```markdown
---
name: review-build-log
description: 根据 StudioX 构建日志定位编译错误，并给出最小修复建议。
---

先调用 project_build_log 读取原始诊断，再检查对应源码与工程配置。
```

打开「插件与工具集 → AI 技能」可查看发现结果并刷新。用户级技能始终可发现；工程级技能需要在这里为当前工程显式启用，同名时工程级技能优先。目录只扫描一层；格式错误、超限或链接目录会显示诊断并跳过。界面启用状态保存在当前用户数据目录的 `ai-skills.json`，不会写进工程或 Git。

模型最初只收到有界的名称和简介；需要具体步骤时才调用 `skill_read` 读取完整正文，引用资料再用 `skill_read_reference` 读取该技能 `references/` 下的 UTF-8 文本。也可调用 `skill_list` 查看完整目录。技能脚本不会自动运行，`allowed-tools` 等字段不授予额外权限；执行编译、Git 写入、调试或串口操作仍走现有 MCP 工具和逐次授权。外部 MCP 客户端可使用同样的三个工具，或自行从标准 `.agents/skills` 目录读取技能。

仓库的 `examples/skills` 提供四份技能：`mcu-build-repair`（构建排错）、`mcu-device-evidence`（器件资料核验）、`mcu-debug-serial`（调试/串口/绘图）和 `mcu-code-style`（嵌入式 C/C++ 风格与正确性）。构建时这些文件复制到程序的 `runtime/skills`，不会改写用户技能目录。用户要覆盖内置技能时，可将同名目录复制到上述用户级目录并修改。

本地器件查询只代表已安装器件包的内容；[Microchip 官方 MCP](https://www.microchip.com/en-us/resources/model-context-protocol-server) 提供该厂商的产品与文档检索，通用 `web_search`/`web_fetch` 可查找其他厂商的公开资料。在线结果可能匹配相邻型号，使用前须核对准确料号、文档版本与本地器件包。STM32、GD、PY 等仍以已安装并核验的 StudioX 包为准，未接入未经验证的大型第三方资料库。

桌面聊天与 `mcp` stdio 命令共用完整 MCP 工具集；不再保留旧四工具提案模式。内置对话保留最近最多六轮历史并按固定顺序提供工具定义，降低重复上下文；实际缓存命中仍取决于所选模型服务商。

## 现有 StudioX.Cli 范围

`src/StudioX.Cli` 是独立命令行入口。下面三个只读命令可供脚本使用；成功时各输出一个 JSON 值：

```powershell
dotnet run --project src/StudioX.Cli -- project-info 'D:\Projects\Blink'
dotnet run --project src/StudioX.Cli -- list-files 'D:\Projects\Blink' src
dotnet run --project src/StudioX.Cli -- read-file 'D:\Projects\Blink' src/main.c
```

`list-files` 省略最后一个参数时列出工程根目录。工程内路径使用正斜杠；`read-file` 保留现有编辑服务的文本编码、文件大小、只读和路径边界检查，返回文本、编码、磁盘哈希等字段。

| 命令 | 当前覆盖 |
|---|---|
| `pack`、`import` | 制作和导入本地器件包 |
| `create` | 根据已安装包创建工程；此 CLI 入口目前不自动初始化 Git 仓库 |
| `build` | 使用指定工具集目录编译工程 |
| `inspect-cubemx`、`import-cubemx` | 检查和导入 CubeMX CMake 工程 |
| `decode` | 调用独立插件宿主解码文本 |

运行 `dotnet run --project src/StudioX.Cli -- help` 可查看完整参数。现有 `build` 命令会在标准输出混合打印进度、原始日志和产物 JSON；需要纯 JSON 的脚本可使用上述三个只读命令。AI 工具通过 `mcp` stdio 命令供外部客户端调用；CLI 没有独立的单轮 Agent 命令。
