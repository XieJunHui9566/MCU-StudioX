# StudioX Plugin SDK

MCU StudioX API 2 的 C# 插件契约，目标为 .NET 10。公开类型不依赖 WPF 或主机实现。

实现 `IStudioXPlugin`，通过 `Describe` 返回命令、面板和 Agent 工具；在 `ActivateAsync` 保存 `IPluginHost`，通过 `CallAsync` 请求声明在 `plugin.json` 的主机工具。工程修改、编译和设备操作继续经过 IDE 授权。面板只提交声明式 JSON 数据，不注入 XAML。

用户插件在独立进程运行，具有当前用户权限。Python、Rust、C++ 使用 API 2 JSON 行进程协议，无需引用这个 .NET 包。API 1 的 `IFrameDecoder` 保留兼容契约。

完整接口、归档格式、协议和示例见源码仓库的 `docs/PLUGINS.md`。
