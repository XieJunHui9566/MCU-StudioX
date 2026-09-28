# 通用插件开发

StudioX 插件提供命令、声明式面板和 Agent 工具。应用核心与正式 SDK 使用 C#/.NET 10；Python、Rust 和 C++ 使用相同的进程协议。插件和安装内容独立于芯片包、工程、主题及采集记录。

## 已实现的边界

| 能力 | 行为 |
|---|---|
| 安装 | `.studioxplugin` ZIP 完整哈希校验、路径和链接校验、原子发布；安装不执行代码 |
| 启用 | 用户显式信任当前清单及文件索引；首次安装和更新默认禁用 |
| .NET 插件 | 独立 `StudioX.PluginHost` 进程加载 `IStudioXPlugin` |
| 进程插件 | 运行插件目录中已索引的固定 EXE；参数中的脚本文件也须在归档内 |
| UI | 宿主渲染纯 JSON 控件，不加载插件 XAML、WPF 程序集或 Web 页面 |
| Agent | 会话启动时固定注册 `AgentTools` 定义；同会话中不动态改写工具说明 |
| 宿主工具 | `hostTools` 白名单经真实应用服务 MCP 路由；保留审批、工程、文件哈希、编辑缓冲和设备所有权检查 |
| 结束 | 离开工程、禁用、更新、卸载和退出停止相关宿主；取消后拒绝迟到面板及宿主调用 |

插件以**当前用户权限**执行。独立进程用于故障隔离及资源回收，不是权限沙箱。SHA-256 证明内容完整性，不证明作者身份；当前没有插件市场、作者签名验证或 OS 权限沙箱。清单中的 `hostTools` 只约束通过 StudioX 桥接的调用，不能阻止受信任插件自行访问用户可访问的文件或网络。

插件启用不授予烧录、下载、串口发送或写工程文件的额外权限。此类请求继续进入已有逐次审批与目标检查。串口和调试继续保持设备单一所有者；UI 与 Agent 的插件会话分别创建，不能擅自接管 IDE 已有连接。

用户内容保存在用户数据目录 `plugins/<id>/plugin.json`，启用授权保存在同目录旁的 `plugins.json`；工程内不保存启用状态。内置插件从安装目录 `runtime/plugins/<id>/plugin.json` 发现。同 ID 的用户插件只有同版本或更高版本可以优先；旧版本不能隐藏较新的内置插件，同 ID/版本不同内容拒绝覆盖。

## 清单

API 2 示例：

```json
{
  "formatVersion": 1,
  "apiVersion": 2,
  "id": "example.overview",
  "version": "1.0.0",
  "displayName": "工程概览",
  "description": "读取当前工程并显示声明式面板。",
  "kind": "dotnet",
  "entryAssembly": "Example.Overview.dll",
  "entryType": "Example.OverviewPlugin",
  "capabilities": ["commands", "panels", "agentTools"],
  "hostTools": ["project_info"],
  "sha256": { "Example.Overview.dll": "由打包工具填充的64位十六进制SHA256" }
}
```

`formatVersion` 保持 1，`apiVersion: 1` 原有 `decode` 插件和 `PluginClient` 短生命周期协议保留。它不会自动迁移到 API 2，也不会作为通用工作区插件启动。API 2 使用 `commands`、`panels`、`agentTools` 能力；能力名称区分大小写，实际贡献必须与清单相符。

`sha256` 必须索引插件目录中除 `plugin.json` 外的全部文件，路径使用正斜杠相对路径。打包工具重建完整索引，不需要开发者手工计算。清单格式、API 版本、未知能力、错误入口、丢失文件、额外文件、链接和哈希变化都会产生原始诊断。

## C#/.NET 10 SDK

正式接口位于 `StudioX.Extensions.Abstractions`，NuGet 包名称为 `StudioX.Plugin.Sdk`。仓库构建生成本地 `.nupkg`；SDK 版本应与目标 StudioX 发行版匹配，不依赖 WPF。可使用本地包源或仓库内项目引用：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="StudioX.Plugin.Sdk" Version="0.2.5" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

接口：

```csharp
PluginContribution Describe();
Task ActivateAsync(IPluginHost host, CancellationToken token);
Task<JsonElement> InvokeAsync(string kind, string id, JsonElement arguments, CancellationToken token);
Task DeactivateAsync(CancellationToken token);

// IPluginHost
Task<JsonElement> CallAsync(string tool, JsonElement arguments, CancellationToken token);
Task PublishPanelAsync(PluginPanelDefinition panel, CancellationToken token);
Task LogAsync(string level, string message, CancellationToken token);
```

`Describe` 返回固定的 `Commands`、`Panels`、`AgentTools` 数组。宿主先验证贡献，再调用 `ActivateAsync` 并开放宿主工具。调用 `kind` 为 `command` 或 `agentTool`，`id` 必须已声明。每个实例串行处理显式调用；宿主工具回调不能递归调用 `plugin_*` 工具。所有等待和后台工作应传递取消令牌，在 `DeactivateAsync` 释放订阅和自有资源。

完整样例见 [WorkspaceOverviewPlugin](../examples/StudioX.SamplePlugin/WorkspaceOverviewPlugin.cs)。它提供真实工程信息表格、串口只读状态和终端文本、累计接收字节曲线、表单回显以及 Agent 只读工具。未连接时明确显示未连接。曲线时间标记为插件宿主的单调时钟，终端文本已经宿主编码与 ANSI 处理，不能作为原始 RX 字节还原。

### 构建和打包

仓库内快速入口：

```powershell
./tools/Build-PluginSample.ps1 -OutputDirectory ./artifacts/my-plugin-build
```

输出目录必须不存在。脚本发布示例、生成 SDK 包，并生成 `studiox.workspace-overview-1.0.0.studioxplugin`。应用版本号不会因此改变。

自有插件准备独立发布目录，将清单模板命名为 `plugin.json`，移除源码、PDB 和私有 SDK 契约副本，再执行：

```powershell
StudioX.Cli.exe plugin pack <发布目录> <输出.studioxplugin>
StudioX.Cli.exe plugin list <runtime目录> <用户数据目录>
StudioX.Cli.exe plugin import <runtime目录> <用户数据目录> <归档.studioxplugin>
StudioX.Cli.exe plugin enable <runtime目录> <用户数据目录> <插件id>
StudioX.Cli.exe plugin disable <runtime目录> <用户数据目录> <插件id>
StudioX.Cli.exe plugin remove <runtime目录> <用户数据目录> <插件id>
```

正常使用从桌面插件管理页导入、查看完整诊断并显式启用，无需手工填写这些目录。内置插件可以禁用，不能由插件管理从安装目录卸载。

## 声明式命令与面板

命令字段为 `id`、`title`、`placement` 和可选 `shortcut`。主要位置为 `palette`、`toolbar`、`editorContext`、`projectContext`；也接受 `tools`、`project`、`editor`、`status` 别名。与 IDE 保留快捷键或其他插件冲突时，宿主拒绝该快捷键并记录诊断，命令仍可通过管理页执行。

面板字段为 `id`、`title`、`widgets`。控件字段为 `id`、`kind`、`label`，可选 `value`、`commandId`、`columns`、`children`。发布更新只能更新已声明面板，不能增加新的 Agent 工具定义。按钮及表单只能引用已声明命令。

| `kind` | 数据 |
|---|---|
| `text` | `value` 为字符串或 null |
| `table` | `columns` 为列名，`value` 为行数组，例如 `[["器件", "STM32F407"]]` |
| `tree` | `children` 为声明式子控件 |
| `form` | `children` 为输入控件和按钮；提交 `{ "widgetId":"表单id", "values":{ "子控件id":"输入值" } }` |
| `plot` | `value` 为有限数值点数组，例如 `[{"x":0,"y":12}]` |
| `button` | `commandId` 为已注册命令，`label` 为可见按钮文字 |
| `input` | 字符串值 |
| `number` | 有限数值或 null |
| `checkbox` | 布尔值或 null |
| `select` | `value` 为 `{ "options":[{"label":"概览","value":"overview"}], "selected":"overview" }` |

`metric`、`chart`、`group` 是可接受别名。单插件最多 128 个命令、32 个面板、128 个 Agent 工具；单面板最多 256 个控件、8 层嵌套、表格 2048 行和 32 列。面板 JSON 最多 1 MiB，单控件 `value` 最多 512 KiB，所有数值必须有限。未知控件、任意 XAML、未声明命令或异常 JSON 被拒绝。限制针对数据量及在途工作，不限制插件生命周期内的总调用次数。

## Agent 工具输入 schema

`PluginAgentToolDefinition(id, description, inputSchema)` 的 schema 使用明确的有限 JSON Schema 子集。工具描述和 schema 在工作区会话启动时固定，启停后须重建会话，避免已有 Agent 前缀中的工具定义变化。

支持：

- `type`：`object`、`array`、`string`、`number`、`integer`、`boolean`、`null`，或这些类型的非空不重复数组；根类型必须为单个 `object`。
- `properties`、`required`；每个 object 必须显式声明布尔 `additionalProperties`，推荐设为 `false`。
- array 的单个 `items` schema；`enum`、`const`。
- `minLength` / `maxLength`、`minItems` / `maxItems`、`minimum` / `maximum`、数值形式的 `exclusiveMinimum` / `exclusiveMaximum`。
- 注释数据 `title`、`description`、`default`、`examples`；`default` 不自动注入调用参数。

不支持 `$ref`、`$defs`、远程引用、组合 schema、`pattern`、`format`、元 schema URI 或其他关键字；未知关键字在插件激活前拒绝。schema 最多 64 KiB、16 层，每个 properties 最多 128 项。数组参数最多 2048 项，字符串最多 65536 个 Unicode 标量；实际调用参数按相同 schema 校验后才发送给插件。整数及上下界比较使用 JSON 原始十进制系数和指数，不经过 double 舍入；schema 和参数的数值字面量最多 4096 个字符，指数绝对值最多 1000000，拒绝超限值且不构造巨大指数幂。

```json
{
  "type": "object",
  "properties": {
    "maxRows": { "type": "integer", "minimum": 1, "maximum": 100 },
    "mode": { "type": "string", "enum": ["overview", "log"] }
  },
  "required": ["maxRows"],
  "additionalProperties": false
}
```

需要写操作的插件应仅在用户点击或 Agent 明确调用后请求相应宿主工具，例如已在 `hostTools` 声明的 `project_create_file`。传入实际 `path` 和 `content` 等该宿主工具要求的字段，由真实工具展示审批并执行；`ActivateAsync` 中不得自动发起写入。拒绝审批或取消必须原样交还调用者，不能降级为自行访问文件。

## 主机接入点

`IPluginHost.CallAsync` 和进程协议 `hostCall` 使用相同工具名称与参数对象。声明所需名称到 `hostTools`，按真实 MCP 工具 schema 传参；完整参数和说明可通过 StudioX MCP `tools/list` 发现。

| 范围 | 主要接口 |
|---|---|
| 当前工程 | `project_info`、`project_list_files`、`project_read_file`、`project_search`、`project_qmd_search` |
| 工程修改 | `project_create_file`、`project_edit_file`、`project_patch_file`、`project_create_directory` |
| 外部库与示例 | `external_project_open`、`external_project_list_files`、`external_project_read_file`、`external_project_search`、`external_project_copy` |
| 编译与 Git | `project_build`、`project_build_log`、`git_status`、`git_diff`、`git_stage`、`git_commit`、`git_branch`、`git_remote` |
| 调试与 RTOS | `debug_status`、`debug_control`、`debug_breakpoint_set`、`debug_read_memory`、`debug_disassemble`、`debug_rtos_snapshot` |
| 下载 | `firmware_download_plan`、`firmware_download`；STC 使用 `stc_isp_plan`、`stc_isp_download` |
| 串口与曲线 | `serial_list_ports`、`serial_connect`、`serial_read`、`serial_read_raw`、`serial_send`、`serial_disconnect`、`plot_start`、`plot_snapshot`、`plot_stop` |
| LVGL 与资源 | `lvgl_project_discover`、`lvgl_ui_inspect`、`lvgl_preview_start`、`lvgl_preview_input`、`lvgl_preview_screenshot`、`lvgl_resource_report` |
| 资料与联网 | `device_search`、`device_info`、`pdf_list`、`pdf_inspect`、`pdf_page`、`web_search`、`web_fetch`、`espressif_docs_search`、`espressif_components_search` |

桌面工作区额外提供：

- `editor_read({"path":"src/main.c","offset":0})`：读取实时缓冲区，未打开则读取磁盘；返回 `isDirty`、`diskHash`、当前 `contentHash`、`text` 和 `nextOffset`。偏移使用 UTF-16 位置，分页保留完整 Unicode 字符；文件继续编辑时哈希会变化。
- `editor_open({"path":"src/main.c","line":20,"column":1})`：打开当前工程源码并定位，行列从 1 开始。

没有桌面编辑器的外部会话可读取磁盘，定位返回 `PLUGIN_EDITOR_UNAVAILABLE`。源码路径和敏感文件规则保持与工程 MCP 一致；不接受工程外绝对路径。修改仍通过工程工具事务，脏缓冲区不会被插件磁盘写入覆盖。递归调用 `plugin_*` 主机工具始终拒绝。

## Python、Rust、C++ 进程协议

这三种语言使用 `kind: "process"`，不要求语言绑定或 Node 桥。Rust/C++ 交付原生 EXE；Python 可以交付冻结 EXE，也可以随插件交付完整便携 Python 发行版及脚本。EXE、脚本、运行时 DLL、标准库和许可证都须放入插件目录并纳入 SHA-256 索引。

Python 清单示意：

```json
{
  "kind": "process",
  "entryAssembly": "",
  "entryType": "",
  "entryExecutable": "python/python.exe",
  "arguments": ["-I", "-u", "plugin.py"]
}
```

以上字段合并到完整 API 2 清单，`plugin.py` 与 `python/python.exe` 均位于归档内。使用便携 Python 时配置本地模块与标准库路径，保留发行版许可证。`-u` 确保协议及时刷新，`-I` 帮助避免开发者环境污染。StudioX 不搜索系统 Python，不执行 shell 参数字符串，不下载语言 SDK，也不把开发者绝对工具路径写入清单。

宿主设置工作目录为插件目录，使用参数列表启动，不通过 CMD、PowerShell 或系统脚本宿主。子进程不继承 IDE 密钥、令牌、代理或用户配置环境变量；只保留 Windows 基础运行所需环境。插件若依赖原生运行时，应使用静态链接或附带必要 DLL，不假设 IDE 的 PATH 或本机开发环境存在。

### JSON Lines 版本 2

标准输入、标准输出使用 UTF-8，每行一个 JSON 对象。标准输出只能写协议；普通日志写 stderr 或 `log` 事件，每行发送后刷新。请求 ID 由发送方生成，双向请求 ID 独立，响应必须复制对应请求 ID。

```json
{"protocolVersion":2,"kind":"request","requestId":"a1","method":"describe","payload":{}}
{"protocolVersion":2,"kind":"response","requestId":"a1","payload":{"commands":[],"panels":[],"agentTools":[]}}
{"protocolVersion":2,"kind":"request","requestId":"a2","method":"activate","payload":{}}
{"protocolVersion":2,"kind":"response","requestId":"a2","payload":{}}
{"protocolVersion":2,"kind":"request","requestId":"a3","method":"invoke","payload":{"kind":"agentTool","id":"overview","arguments":{}}}
{"protocolVersion":2,"kind":"request","requestId":"p1","method":"hostCall","payload":{"tool":"project_info","arguments":{}}}
{"protocolVersion":2,"kind":"response","requestId":"p1","payload":{"name":"当前工程"}}
{"protocolVersion":2,"kind":"response","requestId":"a3","payload":{"name":"当前工程"}}
```

`describe` 返回同一 SDK 贡献数据；`activate` 和 `deactivate` 返回对象。插件通过 `hostCall` 发起工具请求，只有贡献验证并激活前置授权后才能调用。`event` 不需要响应：

```json
{"protocolVersion":2,"kind":"event","method":"panel","payload":{"id":"overview","title":"概览","widgets":[]}}
{"protocolVersion":2,"kind":"event","method":"log","payload":{"level":"info","message":"读取完成"}}
{"protocolVersion":2,"kind":"cancel","requestId":"a3"}
{"protocolVersion":2,"kind":"response","requestId":"a3","errorCode":"PLUGIN_CANCELLED","error":"调用已取消"}
```

等待 `hostCall` 响应期间仍必须读取取消及双向消息。迟到响应不能交给其他请求。耗时任务应在可取消的工作线程或异步任务中执行，由专门读取循环持续排空协议；不要让计算或设备等待阻塞 stdio 读取。宿主单行限制为 1 MiB 字符，单次调用及主机回调保护超时为 15 分钟，覆盖完整 SDK 构建的多个工具阶段；声明和激活分别有更短超时。协议保护不改变应用工具自身的超时，也没有生命周期总调用次数上限；用户可以中途取消，结束会话最终终止进程树。stderr 保留有界原始诊断。

完整参考：

- [Python 标准库进程示例](../examples/plugins/python/plugin.py) 和 [发布说明](../examples/plugins/process-demo/README.md)。
- [Rust 原生示例](../examples/plugins/process-demo/rust/src/main.rs)，开发依赖 `serde_json`。
- [C++ 原生示例](../examples/plugins/process-demo/cpp/main.cpp)，开发依赖本地 `nlohmann_json` 3.11 及 C++20。

Rust/C++ 模板演示短时只读请求及双向取消处理；当前仓库未将 Rust/C++ 编译器与开发依赖自动分发。实际编译需要开发者已有本机工具链；其源码示例不能作为某台实机或每个语言工具链已经验收的证据。
