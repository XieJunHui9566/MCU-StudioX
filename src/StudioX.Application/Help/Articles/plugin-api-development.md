# 插件 API：开发、协议与错误处理

## C# 工程和 SDK

插件使用 .NET 10，引用当前发行版提供的 StudioX.Plugin.Sdk。该包只含公开契约，不依赖 WPF 或内部 Foundation。下面使用本发行版 0.2.6.15；SDK 包源由开发者配置，不假定该包已发布到公开 NuGet 源。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>Example.Overview</AssemblyName>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="StudioX.Plugin.Sdk"
      Version="0.2.6.15" ExcludeAssets="runtime" />
  </ItemGroup>
</Project>
```

在源码仓库中也可直接引用 StudioX.Extensions.Abstractions.csproj。交付时由宿主统一提供契约程序集，不附带私有契约副本；发布目录包含自己的插件 DLL、deps.json 和所需依赖，不把源码模板或 PDB 当成运行文件。

## 一个最小只读插件

下面的入口类有公开无参数构造函数。它声明一个命令和面板，用户调用命令后读取 project_info；激活阶段不读写工程、不连接设备。

```csharp
using System.Text.Json;
using StudioX.Extensions.Abstractions;

namespace Example;

public sealed class OverviewPlugin : IStudioXPlugin
{
    private IPluginHost? host;

    public PluginContribution Describe() => new(
        [new("info", "读取当前工程")],
        [new("overview", "工程概览", [])],
        []);

    public Task ActivateAsync(
        IPluginHost host, CancellationToken token)
    {
        this.host = host;
        return Task.CompletedTask;
    }

    public async Task<JsonElement> InvokeAsync(
        string kind, string id, JsonElement arguments,
        CancellationToken token)
    {
        if (kind != "command" || id != "info")
            throw new ArgumentException("未注册的调用");

        var value = await host!.CallAsync("project_info",
            JsonSerializer.SerializeToElement(new { }), token);
        await host.PublishPanelAsync(new("overview", "工程概览",
            [new("project", "text", "当前工程",
                JsonSerializer.SerializeToElement(value.GetRawText()))]),
            token);
        return value;
    }

    public Task DeactivateAsync(CancellationToken token)
    {
        host = null;
        return Task.CompletedTask;
    }
}
```

Describe 返回的命令、面板和 Agent 工具必须与清单能力相符。Invoke 的 kind 为 command 或 agentTool；API 3 的事件、补全和快照使用其对应贡献类型。每个请求携带取消令牌；不要把长任务或用户输入等待放进协议读取线程。

## 完整清单与打包

将以下内容保存为发布目录内的 plugin.json。EntryAssembly 的文件名须与项目实际输出一致；EntryType 是类的完整名称。sha256 的空对象仅用于打包输入，最终包必须由打包工具重建完整索引。

```json
{
  "formatVersion": 1,
  "apiVersion": 2,
  "id": "example.overview",
  "version": "1.0.0",
  "displayName": "工程概览",
  "kind": "dotnet",
  "entryAssembly": "Example.Overview.dll",
  "entryType": "Example.OverviewPlugin",
  "capabilities": ["commands", "panels"],
  "hostTools": ["project_info"],
  "sha256": {}
}
```

插件自己的 version 为三段整数；它与 IDE / SDK 版本独立。默认 scope 为 project；应用级插件必须显式声明 application，目前只提供无工程宿主工具的命令和面板。清单未知能力、重复能力、错误入口和目录外路径会被拒绝。

```text
StudioX.Cli.exe plugin pack <发布目录> <输出.studioxplugin>
```

打包后在“工具 → 插件与扩展 → 插件管理”导入，核对诊断并显式启用。源码仓库的 tools/Build-PluginSample.ps1 可发布完整 C# 示例并生成本地 SDK nupkg；加 -Development 可构建 API 3 的设置、事件、补全和只读调试快照示例。使用独立输出目录，不覆盖已有用户插件或其授权。

## 调用宿主与保留错误码

IPluginHost 提供 CallAsync、PublishPanelAsync 和 LogAsync，均接受 CancellationToken。CallAsync 使用实际 MCP 工具名与参数对象。常用只读入口为 project_info、project_list_files、project_read_file、project_search；编辑器实时内容用 editor_read，定位用 editor_open。完整工具 schema 通过 StudioX MCP tools/list 发现，不自行猜测参数。

错误通过公开 SDK 的 PluginHostException 提供稳定 Code 和原始 Message；它不要求插件引用内部 StudioX.Foundation。仅在确实能恢复的情况下处理错误，其余保留原错误码重新抛出。调用者取消仍为 OperationCanceledException，不转成普通失败，也不要在取消后继续后台写入。

```csharp
try
{
    return await host.CallAsync(tool, arguments, token);
}
catch (PluginHostException error)
    when (error.Code == "MCP_APPROVAL_DENIED")
{
    await host.LogAsync("warning", error.Message, token);
    throw; // 保留审批拒绝的原始 Code
}
catch (OperationCanceledException) when (token.IsCancellationRequested)
{
    throw; // 只结束当前调用，清理自己的资源
}
```

hostTools 是声明白名单，不是授权结果。工程写入需按具体操作审批、校验文件哈希并保护脏缓冲区；串口、下载和调试沿用设备会话所有权。插件不能递归请求 plugin_* 工具。不能通过直接磁盘或设备访问绕过被宿主拒绝的同一操作。

## Python、Rust、C++ 与其他进程语言

进程插件把 kind 改为 process，entryAssembly / entryType 为空，entryExecutable 指向包内固定 EXE。Rust / C++ 可以发布原生 EXE；Python 可以冻结为 EXE，也可携带完整便携运行时和脚本。IDE 不查找用户系统 Python / Node，不下载语言工具；必要 DLL、标准库和许可证均由开发者随包提供并纳入索引。

```json
{
  "kind": "process",
  "entryAssembly": "",
  "entryType": "",
  "entryExecutable": "python/python.exe",
  "arguments": ["-I", "-u", "plugin.py"]
}
```

以上字段合并到完整 API 2 / 3 清单。参数中的脚本须为包内被索引的相对路径。发布 Python 前在没有开发机环境变量和全局 site-packages 的条件下验证标准库、线程、取消与退出。Rust / C++ 源码模板位于 examples/plugins/process-demo，Python 位于 examples/plugins/python；本轮验证 C# 和 Python，其他语言模板不等于其所有开发环境已通过。

## JSON Lines v2 消息

stdin / stdout 使用 UTF-8，每行一个 JSON 对象，写入后 flush。stdout 只写协议；日志使用 stderr 或 log 事件。stdio 包络中的 protocolVersion 始终为 2，清单 apiVersion 为 3 时也不改变。

```json
{"protocolVersion":2,"kind":"request","requestId":"h1","method":"describe","payload":{}}
{"protocolVersion":2,"kind":"response","requestId":"h1","payload":{"commands":[],"panels":[],"agentTools":[]}}
{"protocolVersion":2,"kind":"request","requestId":"h2","method":"activate","payload":{}}
{"protocolVersion":2,"kind":"response","requestId":"h2","payload":{}}
{"protocolVersion":2,"kind":"request","requestId":"h3","method":"invoke","payload":{"kind":"command","id":"info","arguments":{}}}
{"protocolVersion":2,"kind":"request","requestId":"p1","method":"hostCall","payload":{"tool":"project_info","arguments":{}}}
{"protocolVersion":2,"kind":"response","requestId":"p1","payload":{"name":"example"}}
{"protocolVersion":2,"kind":"response","requestId":"h3","payload":{"name":"example"}}
```

请求 ID 由发送方生成，响应必须复制对应 ID；双向请求有独立的在途集合。hostCall 只在贡献校验和激活授权之后开放。等待宿主响应期间仍须读取 response / cancel，不让计算或设备等待堵塞收包。长任务使用可取消工作队列；普通命令串行处理，不递归调用自己。

```json
{"protocolVersion":2,"kind":"event","method":"panel","payload":{"id":"overview","title":"概览","widgets":[]}}
{"protocolVersion":2,"kind":"cancel","requestId":"h3"}
{"protocolVersion":2,"kind":"response","requestId":"h3","errorCode":"PLUGIN_CANCELLED","error":"调用已取消"}
```

panel / log 无需响应并按接收顺序处理；读取循环不会等待慢事件，仍可完成双向响应和取消。事件队列上限 64，超限明确失败。宿主每个方向最多 32 个在途请求，单行最多 1,048,576 个 .NET 字符；不是插件生命周期总调用数限制。通用调用及宿主回调保护超时为 15 分钟，声明、激活和专用补全 / 快照调用另有更短期限。会话退出最终回收进程树，stderr 保留有界原始诊断。

## 常见错误与排查

| 诊断 | 检查方向 |
| --- | --- |
| PLUGIN_API / PLUGIN_MANIFEST | 对齐 API 与清单版本，删除未知或重复能力，核对前置能力 |
| PLUGIN_HASH / PLUGIN_ENTRY | 重新打包实际文件，检查入口类、固定 EXE 和依赖索引 |
| PLUGIN_HOST_TOOL | hostTools 声明、实际工具名称、参数对象及递归限制 |
| MCP_APPROVAL_DENIED | 用户拒绝了具体操作；保留拒绝并结束，不绕过授权 |
| PLUGIN_PROTOCOL / PLUGIN_PROTOCOL_LIMIT | stdout 是否混入日志、协议字段、行长度及响应 ID |
| PLUGIN_BUSY | 在途请求或事件积压；合并重复状态、缩短事件处理 |
| PLUGIN_EVENT_CANCELLED | 事件回调在连接仍有效时自行取消；检查回调令牌，保留原始异常并重新启用插件 |
| PLUGIN_TIMEOUT / PLUGIN_EXIT | 取消是否被处理、子进程是否退出、stderr 原始错误 |

先查看“插件日志与诊断”，保留第一条原始错误，再检查具体版本和发行文件。不要为了修复插件而清空全部器件包、覆盖用户源码或启动未授权硬件验证。旧 API 1 只能作为解码插件运行；语言补全不是完整 LSP，调试快照不是硬件驱动，完整 MCU 配置草案也不等于现有试行能力。
