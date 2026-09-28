# C# 插件示例

`WorkspaceOverviewPlugin` 使用 .NET 10 SDK，实现一个工作台命令、声明式面板和一个 Agent 工具。所有工程和串口数据通过 `IPluginHost.CallAsync` 读取。插件不建立串口连接；未连接时显示真实的未连接状态。

构建后将 DLL 和必要依赖放入独立目录，以 `plugin.template.json` 为 `plugin.json` 输入，通过插件打包工具生成完整 SHA-256 文件索引。不要把源码模板清单一起放进最终 payload。打包及安装步骤见 [插件开发指南](../../docs/PLUGINS.md)。

首次安装和更新默认禁用。显式启用后，插件在独立 `StudioX.PluginHost` 进程内以当前用户权限运行。界面刷新与 Agent 调用具有独立工作区会话；它们不会接管 IDE 的串口连接。
