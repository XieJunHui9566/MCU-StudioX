# 自定义解码插件示例

插件只引用 `StudioX.Extensions.Abstractions`，实现 `IFrameDecoder`。输入是字节帧，输出是摘要和命名信号。例子接收 `24.5,1`，返回 temperature 与 digital 两个值。

`tools/Publish.ps1` 编译示例并生成包含 SHA-256 文件索引的 `plugin.json`，将插件和独立宿主放入发行目录。不在桌面进程加载用户程序集。

清单字段：`formatVersion: 1`、`apiVersion: 1`、`id`、`version`、`displayName`、`entryAssembly`、`entryType`、`capabilities: ["decode"]`、`sha256`。插件全部文件（清单本身除外）必须出现在索引中。自定义依赖应随插件一起分发；不要分发第二份 Abstractions 合同程序集，由宿主共享合同。

宿主通过标准输入/输出交换版本 1 JSON 请求/响应，请求带唯一 ID。普通 `Console.WriteLine` 被转到 stderr，保留 stdout 给协议。单请求 64 KiB，5 秒超时；首版按次启动宿主，适用于接口演示，尚未实现高频持续数据处理。超时、崩溃和插件异常不能表示为成功。

当前插件以用户权限执行。独立进程用于故障隔离，不是防恶意代码的操作系统沙箱。扩展市场、签名、授权界面、持续宿主和 UI 插件属于后续工作。
