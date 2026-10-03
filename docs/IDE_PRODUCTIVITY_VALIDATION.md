# IDE 开发效率扩展验收

日期：2026-09-29。基于本地 0.2.5.2 开发工作区；本轮面向通用 IDE，不绑定 AG32。保留原有未提交修改，未调整产品版本、安装日常版本或发布 GitHub。

## 已接入的能力

| 能力 | 用户入口与实现范围 |
|---|---|
| 格式化、快捷修复 | C/C++ 文件及选区格式化；clangd 当前文件文本修复；先显示差异再应用，可以撤销。Ctrl+Alt+F、Alt+Enter、编辑菜单与右键菜单 |
| 快速检索、统一命令 | Ctrl+P 文件、Ctrl+T 工程符号、Ctrl+Shift+P 菜单与插件命令；迟到检索取消，结果更新期间不能误选旧项 |
| 本地历史 | 保存和批量编辑前记录；按时间查看、比较和恢复；每文件最多 50 份、32 MiB，独立于 Git 与恢复草稿 |
| 分组与布局 | 两组编辑器、标签拖动和排序、合并、独立撤销；保存分组所选标签、草稿、光标、选区和布局 |
| 故障处理 | 工具、头文件、端口、连接、文件冲突等规则；提供处理步骤及操作入口，保留原始诊断 |
| 开发环境组件 | 内置版本、空间、文件、组件版本、工程主开发环境组件与引脚映射依赖；完整校验、离线导出、同版本修复和原目录备份 |
| 插件 API 3 | 设置表单、工程/文档事件、自定义后缀补全、只读调试快照视图；独立宿主和可打包 C# 示例；API 2 回归 |

操作及数据边界见 [编辑工作台](EDITOR_WORKSPACE.md)、[插件文档](PLUGINS.md)。

## 自动化验证

使用已有本机开发环境组件、实际 clangd、实际独立插件进程和 WPF 窗口。测试工程与用户数据位于 `artifacts/validation`，没有连接、下载或调试硬件。

| 验证 | 结果 | 原始记录（工作区相对路径） |
|---|---|---|
| 新增应用服务、插件兼容和大目录边界 | 36 PASS | `artifacts/validation/ide-next-final2/result.txt` |
| 工程编辑、C/C++ 引用与重命名、实时诊断、草稿边界 | 40 PASS | `artifacts/ide-next-workspace-regression.log` |
| Python 导航、查找替换回归 | 27 PASS | `artifacts/ide-next-python-regression.log` |
| 应用架构与资源释放 | 13 PASS | `artifacts/ide-next-architecture.log` |
| 桌面职责与状态回归 | 61 PASS | `artifacts/ide-next-desktop-architecture.log` |
| WPF 格式化、撤销、分屏、布局、工具与指南 | 12 PASS | `artifacts/validation/ide-next-ui-final2/next-ui-result.txt` |
| 第二个 WPF 进程恢复两个分组与所选文档 | 3 PASS | `artifacts/validation/ide-next-ui-final2/recovery-next-result.txt` |
| 原编辑工作台 WPF 回归及重启恢复 | 12 + 4 PASS | `artifacts/validation/ide-next-editor-ui-regression/*result.txt` |

共 208 项检查通过。离线修复测试覆盖有效归档、错误版本、损坏文件、路径越界、符号链接、失败后保留原版本；测试使用隔离的工具目录。插件示例通过 `Build-PluginSample.ps1 -Development` 真实构建并生成 `.studioxplugin`，并非只校验 JSON。

最终 `tools/Build.ps1 -BuildArtifactsDirectory artifacts/build/ide-next` 完整构建成功，0 警告、0 错误，日志为 `artifacts/ide-next-final-build.log`。开发程序位于 `artifacts/build/ide-next/bin/StudioX.Desktop/debug_win-x64/MCU StudioX.exe`；插件归档与 SDK 位于 `artifacts/validation/ide-next-plugin-sdk-release`。这次验证没有制作或覆盖安装包。

合成性能夹具含 12,000 个小头文件：快速打开查找 24 ms，有界工程搜索达到结果上限并明确拒绝耗时 86 ms；测试进程工作集约 72 MiB。这些数值只说明该本机夹具，不代表所有大型 SDK、整机内存或长期稳定性结果。

## 模拟用户操作

在真实桌面窗口中通过鼠标与键盘操作，未用控件方法调用替代以下交互：

1. Ctrl+P 搜索文件并 Enter 打开；Ctrl+T 搜索 `helper` 并跳到定义。
2. Ctrl+Alt+Right 分组；鼠标跨组拖动标签并合并。
3. Ctrl+Alt+F 生成格式化差异，点击应用后 Ctrl+Z 撤销。
4. 输入缺分号的 C 代码，观察实时错误，选择快捷修复并应用；另验证 Alt+Enter 入口。
5. 打开本地历史、选择旧版本、查看差异并恢复。
6. 关闭再启动，核对未保存内容、两个可见代码页及活动文档。
7. 打开开发环境组件，查看本机 11 个开发环境组件；经故障指南中的按钮进入相同页面。
8. 修改插件问候语并保存；在 `.sxdemo` 中 Ctrl+Space，确认新设置出现在实际补全说明中，Enter 插入代码。
9. 打开调试快照扩展，确认显示 `Disconnected`、`hardware: false`，未启动硬件会话。

测试中发现并修复：命令可用状态未刷新、快速拖动漏触发、窄分组被文件结构挤压、工具清单逐文件重复路径检查、检索框短暂保留旧结果、重启后另一组停留在欢迎页。标点快捷键在本机没有可靠触发，新增并验证 Alt+Enter，保留 Ctrl+. 兼容绑定。

截图来自实际交互窗口：

![重启后两组代码与草稿恢复](../artifacts/validation/ide-next-native/recovered-split.jpg)

![快捷修复差异](../artifacts/validation/ide-next-native/quick-fix.png)

![插件设置参与自定义语言补全](../artifacts/validation/ide-next-native/plugin-completion.png)

![内置开发环境组件](../artifacts/validation/ide-next-native/tool-environment.png)

## 当前边界

格式化和快捷修复当前针对 C/C++，不是所有语言的格式化器。插件语言接口提供补全；调试接口提供只读快照解释，尚不是完整 LSP/DAP 驱动。进程隔离不等同权限沙箱。源代码撤销栈不跨重启持久化。硬件操作与真实芯片结果不在此次验收范围内。
