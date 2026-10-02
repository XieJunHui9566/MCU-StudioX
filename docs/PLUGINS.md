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

插件在清单中声明可选的 `activity` 即可在左侧竖栏注册独立入口，点击打开自身的标签页。名称、提示和图案都来自插件包，宿主没有插件 ID 或图标目录白名单；添加新的插件不需要修改、编译 IDE。标签关闭只隐藏页面并保留本次会话输入；禁用、崩溃或结束工程会话时移除入口和页面。未声明入口的插件保持原有行为，面板仍显示在插件管理页。无面板插件使用通用提示页，原命令面板入口继续可用。

有面板插件的 `tools` / `palette` 命令会打开所属插件页面，便于查看计算结果；编辑器和工程上下文命令保留原有页面焦点。

`activity.version` 当前为 1。`title` 可省略，默认使用 `displayName`，指定时为 1–80 字符且不含控制字符；`tooltip` 可省略，最长 1024 字符。`icon.strokes` 是 24×24 坐标系的折线数组，每条为 `[x1,y1,x2,y2,...]`：1–32 条，每条 2–128 个点，总计最多 1024 个点，坐标为 0–24 的有限数值。闭合图案重复起点；省略 `icon` 使用通用插头。图标自动随主题、选中状态和 DPI 变色缩放，竖栏支持滚动。该接口仅解析数值数据，不执行 XAML、SVG 文档或插件 UI 代码。

```json
"activity": {
  "version": 1,
  "title": "我的工具",
  "tooltip": "插件自己的说明",
  "icon": { "strokes": [[3,12,12,3,21,12,12,21,3,12]] }
}
```

清单格式仍为 1，API 2 / 3 插件均可声明此入口；旧宿主忽略此可选字段、继续显示原扩展面板。四个实验室插件的完整声明在 `examples/StudioX.LabPlugins/manifests/`，不在 IDE 的图标表或 C# 功能分支中。打包 CLI 必须为支持 `activity` 的构建，否则旧工具重新序列化清单时会丢弃该字段；构建脚本会检查这一点。

可直接构建的四个实验插件见 [实验室插件](../examples/StudioX.LabPlugins/README.md)：位运算、协议校验、波形和像素图案。使用 `tools/Build-LabPlugins.ps1 -OutputDirectory <new-directory>` 生成四个独立 `.studioxplugin` 归档。它们不声明宿主工具，不读写工程或设备，也不依赖系统安装的 Python/Node。

第二批 [创客插件](../examples/StudioX.MakerPlugins/README.md) 提供 RGB 灯效、蜂鸣器旋律、PID 调参沙盒和按键消抖。PID 包含分页面板、对象/延迟/扰动/噪声、抗饱和与微分滤波、指标、基线比较及 C 控制器。使用 `tools/Build-MakerPlugins.ps1` 和已有打包 CLI 独立交付，不重新编译 IDE。

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


## API 3：设置与开发扩展

API 1 解码和 API 2 通用插件继续可用；`PluginContribution` 的原三参数构造保持不变。新能力显式使用 `apiVersion: 3`，清单格式仍为 1，stdio 包络的 `protocolVersion` 仍为 2。API 3 在既有命令、面板、Agent 工具之外增加以下可选数组，并要求清单声明对应能力：

| 贡献 / 能力 | 约束与调用 |
|---|---|
| `settings` | 最多 64 个 string / boolean / integer 设置；声明默认值、说明及整数范围，宿主生成设置页面 |
| `events` | `project.opened`、`project.closing`、`document.opened`、`document.changed`、`document.saved`、`document.closed`、`settings.changed` |
| `languages` | 最多 16 个语言定义及后缀；`invoke(kind: language, id: providerId)` 收到 `PluginLanguageRequest` |
| `debugAdapters` | 最多 16 个调试快照视图；`invoke(kind: debugAdapter, id: adapterId)` 收到宿主当前状态与快照，返回无动作的 `PluginPanelDefinition` |

设置保存在独立用户目录 `plugin-settings/<id>.json`，安装升级不会覆盖；保存前校验字段、类型与范围。若订阅 `settings.changed`，启动及保存后收到有效设置对象。文档事件只发送相对路径、是否未保存与字符数，连续变化按文件合并；读取正文继续走已有编辑器宿主工具。工程事件、补全及调试快照调用有短超时，停止插件撤销在途调用。通用命令的既有操作授权没有改变。

语言补全请求含 `operation: completion`、`path`、`text`、`offset` 和 `revision`，位置为 UTF-16。正文最多 256 Ki 字符，返回最多 256 个 `PluginCompletion`（label、insertText、detail）。桌面在自定义文件后缀上接入 Ctrl+Space 及输入补全，丢弃过期编辑结果；已有 C/C++、Python、CMake 服务保持优先。此接口当前没有宣称提供自定义语言诊断、语义重命名或完整 LSP。

调试扩展入口是插件管理页的“打开调试扩展”或“工具 → 调试快照扩展”。管理页可以按中文能力、名称、ID、说明搜索及筛选，运行成功的设置和调试贡献提供直接入口。

每个 `(pluginId, adapterId)` 在当前工作区只有一个独立视图。暂停、单步、更新观察项和选择栈帧后自动刷新；运行、断开、切换工程立即清除结果。关闭标签释放订阅和后台解释，禁用/崩溃撤销结果，工作区结束移除相关标签。解释在后台串行处理，80 ms 合并连续刷新，队列最多保留一个最新请求；5 秒超时或原始异常显示在视图，暂停时可手动重试。迟到响应不能恢复过期面板。

调试器在同一命令锁内捕获已有快照，不发送新的 MI 查询。API 3 原有 `state`、`hardware`、`snapshot` 字段保持，新宿主添加 `formatVersion: 1`、`revision`、`reason`。SDK `PluginDebugSnapshotRequest` 提供相同输入契约，`revision` 是视图请求版本，不是设备时钟；仅 `state: "Stopped"` 的 `snapshot` 含有效数据。状态不可用或工程不一致时快照为空。返回仍是纯数据 `PluginPanelDefinition`，未知字段、未声明命令和非法控件拒绝并保留诊断。

它不启动连接，不持有设备，不提供任意 DAP 连接器、烧录器驱动或绕过会话授权的发送入口；独立插件进程仍不是权限沙箱。插件不得在解释函数中等待用户输入或发起目标控制。`DebugSnapshotPanel.cs` 示例将快照呈现为寄存器、调用栈、局部变量和观察项表格，不引用调试器实现或特定 MCU。

实际 C# 示例为 `examples/StudioX.SamplePlugin/DevelopmentToolsPlugin.cs`，配套 `development.template.json`，包含可修改的问候语、事件计数、`.sxdemo` 补全和调试快照概览。使用 `tools/Build-PluginSample.ps1 -Development -OutputDirectory <new-directory>` 构建归档；不加 `-Development` 仍构建原 API 2 工程概览示例。插件安装和启用沿用原有界面流程。
