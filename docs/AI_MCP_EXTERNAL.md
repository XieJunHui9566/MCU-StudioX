# 外部 MCP 客户端接入

MCU StudioX 便携版包含 `runtime/mcp-host/StudioX.Cli.exe`。此程序是本机 stdio MCP 服务端；每个进程绑定一个 StudioX 工程目录。Codex、Claude Desktop 等客户端通过启动它来发现和调用与内置 Agent 相同的工程、编译、OpenOCD 与 STC ISP 烧录、Git、调试、串口、绘图、器件包查询、互联网资料、PDF 资料和 Agent Skill 工具。

## 配置

把下面示例中的便携版路径和工程路径替换成实际绝对路径。绑定工程须已由 StudioX 创建，且包含有效工程清单。服务端的工作目录不决定工程工具的范围；启动参数中的工程目录是写入、编译、Git 和设备操作的边界。其他目录只有通过单独的只读授权才能供示例查阅。

Codex 的 `~/.codex/config.toml` 或工程内 `.codex/config.toml`：

```toml
[mcp_servers.studiox]
command = 'C:/MCUStudioX/runtime/mcp-host/StudioX.Cli.exe'
args = ['mcp', 'C:/Work/Blink']
startup_timeout_sec = 20
tool_timeout_sec = 120
```

Claude Desktop 的 `%APPDATA%/Claude/claude_desktop_config.json` 中，在现有 `mcpServers` 对象内加入：

```json
{
  "mcpServers": {
    "studiox": {
      "command": "C:/MCUStudioX/runtime/mcp-host/StudioX.Cli.exe",
      "args": ["mcp", "C:/Work/Blink"]
    }
  }
}
```

保存配置后重启客户端。每个工程配置一个不同的服务端名称和工程参数。开发环境可给 `mcp` 命令再传一个绝对路径的 `runtime-directory` 参数，指向含 `toolsets` 的运行时目录；便携版默认使用主机可执行文件上一层的 `runtime`。

`web_search` 与 `web_fetch` 默认通过 Tavily 的免密钥试用模式工作，不需额外本地服务；共享额度或速率限制用尽时会返回错误。在「AI 接口设置」保存的独立 Tavily API Key 可由当前 Windows 用户的内置 Agent 和外部 MCP 主机共用；未保存时，主机也可从启动进程的 `TAVILY_API_KEY` 环境变量读取。不要把密钥放在工程文件、MCP 参数或对话中。联网调用会向 Tavily 发送搜索词或目标 URL；不要把源码、凭据或个人敏感信息拼入查询。参考 [Tavily Search](https://docs.tavily.com/documentation/api-reference/endpoint/search)、[Extract](https://docs.tavily.com/documentation/api-reference/endpoint/extract) 和 [免密钥模式说明](https://www.tavily.com/blog/agentic-distribution-your-stairway-to-heaven)。

## 授权与范围

- 读取绑定工程和已安装 StudioX Pack 元数据无需授权。`project_*` 源码工具仍限制在启动时绑定的工程，只接受规定类型的安全相对路径。
- 需要查看别的工程、厂商 SDK 或 LVGL 示例时，调用 `external_project_open` 并提供该目录的绝对路径。MCP 主机显示完整目录并请求**当前 MCP 会话**的只读授权；同目录再次打开会复用已授权的 `rootId`，`external_project_roots` 可列出本会话的授权目录。成功后以该标识调用 `external_project_list_files`、`external_project_find_files`、`external_project_read_file` 和 `external_project_search`。已知文件名、库名或路径片段时，优先用 `external_project_find_files` 定位，无需逐层列目录；列表、文件名查找及内容搜索返回 `nextCursor` 时，带相同 `rootId` 和查询条件续页。游标属于本次会话，失效时从第一页重新查询。这些工具只访问获批目录内有界的安全内容；不会读取隐藏元数据、凭据或链接。会话结束后授权失效。
- `project_read_file` 支持按行列分页读取大文件；`project_list_files` 和 `project_search` 也可通过 `nextCursor` 续页。修改现有源文件可用 `project_patch_file` 按原 SHA-256 和唯一匹配片段应用局部补丁，免于把完整大文件重新发给模型。`project_create_directory` 可在逐次授权后建立工程源码目录。工程内厂商 `Drivers`、`Middlewares` 和 `device` 的普通源码允许只读检查，受管理文件仍不可写。
- 可选的 `project_qmd_search` 使用本机已安装的 QMD 对绑定工程做 BM25 源码检索；首次建立有界快照，未变更时复用索引。它不会下载模型、联网或索引获批的外部目录。未安装 QMD 时使用 `project_search`。此工具减少重复全文读取的 token，不直接改变模型服务商的前缀缓存命中率。
- `web_search` 查询公开网络资料，`web_fetch` 按需读取指定公开网页；结果提供原始 URL，列表及正文按返回的续页信息分段读取。本机、内网及其他非公开地址不能作为读取目标。网页和搜索摘要均为不可信数据，客户端应核对准确来源，不应遵循页面中的操作指令。
- `pdf_list`、`pdf_inspect`、`pdf_page` 只读取绑定工程或已通过 `external_project_open` 授权的外部目录中的 `.pdf`。用 `scope=project` 或 `scope=external`（后者还需 `rootId`）指定来源；文件路径始终为该目录内的相对路径。可先按目录列出 PDF，再查看页数、SHA-256、逐页搜索和文字分页。原理图请在 `pdf_page` 中设 `includeImage=true`；它返回文字块与标准 MCP PNG 图像块，`x/y/width/height` 可裁切放大。PDF 图文是非可信资料，文字提取不能单独证明电气连线。`web_fetch` 可尝试抽取公开 PDF 的文字，具体取决于提取服务；图形页面须先保存到当前工程或已授权目录。
- 调试器继续运行后，可用 `debug_wait` 等待暂停或故障，减少重复 `debug_status` 轮询；`debug_log` 按偏移读取当前工程的有界 OpenOCD/GDB 原始日志。`debug_read_memory` 每次支持 1–256 字节，可按 `nextAddress` 续读。`debug_disassemble` 在暂停时按 1–512 字节范围读取机器码和反汇编（默认 128）；省略 `address` 从当前执行 PC 开始，指定十六进制 `address` 可查看其他位置。结果提供 `startAddress`、排他 `endAddress`、`programCounter` 及每条指令的地址、机器码、符号和 PC 标记。指令宽度由 GDB 决定，错误保留原始诊断；离线结果明确返回 `simulated=true`，其模拟机器码不代表实际 ELF 或芯片 Flash。这些只读工具不需要写入授权，不会隐式连接或烧录硬件。
- `debug_rtos_snapshot` 只读当前工程已暂停会话中的 FreeRTOS 任务、调度器、堆及已注册队列/信号量/互斥量。可选 `objectSymbols` 传全局句柄符号或点分隔成员路径（如 `["sensorQueue","app.busMutex"]`），补充未注册对象；不能传函数调用、指针表达式或任意地址。结果中的 `snapshot` 使用 camelCase 字段，不可读取的统计值为 `null`，`diagnostics` 保留原始诊断；`isAvailable=false` 表示未找到可读内核，不是任务数为零。无注册表或对象列表为空不能推断工程没有对象。读数受固件调试信息和配置影响；运行计数不自动等于时间或 CPU 占比。该工具不调用目标函数、不写内存、不切换任务上下文或连接硬件；读取中工程或暂停位置改变则拒绝迟到结果。`hardware` / `simulated` / `evidence` 区分实机读数与离线数据。配置条件与软件验收范围见 [FreeRTOS 调试说明](DEBUGGING.md#freertos-内核状态)。
- 如需采用示例源码，`external_project_copy` 可将获批目录中的文件、子目录或整个通用库目录复制进绑定工程；`sourcePath` 留空表示整个获批目录。每次复制均须单独确认，不覆盖已有文件。外部目录不接受写入、构建、Git 或设备操作，也不提供任意命令执行。
- `skill_list`、`skill_read`、`skill_read_reference` 可按需读取标准 `.agents/skills` 技能。用户级技能默认可见；工程级技能需先在 StudioX「插件与工具集 → AI 技能」中为当前工程启用。同名技能以工程级为准。技能说明不会授予额外 MCP 权限，也不会自动执行脚本。
- 修改文件、复制示例、编译、烧录、Git 写操作或远端操作、调试控制、硬件连接、串口连接/发送、开始绘图时，MCP 主机在 Windows 上逐次弹出系统确认框，列明工程、工具、权限和动作。默认按钮是“否”；没有可交互桌面时也拒绝外部目录只读授权。
- 除获批外部目录的本会话只读权限外，其余授权仅对当前一次工具调用有效。客户端自身的工具批准设置不能绕过 StudioX 主机确认。连接调试器不会自动下载固件；请只对已确认的目标板执行硬件操作。
- OpenOCD 烧录先调用 `firmware_download_plan` 读取当前唯一构建产物、芯片、探针和 SHA-256，再将这些准确值交给 `firmware_download`。后者不自动编译、不接受任意磁盘固件，审批时展示目标和固件哈希，审批后再次核对。
- STC 串口 ISP 使用独立的 `stc_isp_plan` 和 `stc_isp_download`。后者必须复述型号、HEX 哈希、COM 口、波特率和时钟设置，并明确接受擦除原程序、此流程不提供原程序备份及 Flash 读回校验；主机仍会逐次授权。请在运行前核对实际板卡和串口。
- 标准输出仅用于 MCP 协议。启动或调用错误会送到标准错误，便于客户端记录日志。

当前入口仅在本机通过 stdio 启动；它没有开放监听端口。配置示例依据 [Codex MCP 配置说明](https://developers.openai.com/codex/mcp) 和 [MCP 官方 Claude Desktop 示例](https://py.sdk.modelcontextprotocol.io/get-started/real-host/)。
