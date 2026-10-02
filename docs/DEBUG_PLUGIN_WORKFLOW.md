# 调试扩展与插件发现

这一轮完善既有 API 3 调试快照视图的完整使用链，不改产品版本号。

## 使用流程

1. 从“工具 → 插件与工具集”导入 `.studioxplugin`。审阅版本、说明、来源和声明能力，显式启用当前内容。
2. 打开工程，在管理页按“调试快照”筛选，点击“打开调试扩展”。有设置贡献的插件也可从该行直接打开设置。
3. 使用正常调试入口连接并暂停，视图自动解释已有快照。单步、观察项或栈帧变化触发刷新；运行或断开清除结果。
4. 解释错误显示原始诊断，暂停时可以重新解释。关闭标签取消在途调用；停用、崩溃和工程结束撤销视图。

扩展在独立进程运行，显示由宿主生成的声明式数据界面。它不增加器件、探针或下载驱动，进程隔离也不代表权限沙箱。用户的设备操作授权仍按具体会话处理。

## 作者入口

`examples/StudioX.SamplePlugin/DevelopmentToolsPlugin.cs` 声明 `debugAdapters`，`DebugSnapshotPanel.cs` 示例使用 SDK 的 `PluginDebugSnapshotRequest` 将当前寄存器、调用栈、局部变量和观察项转为表格。示例没有宿主工具声明，也不操作设备。

```powershell
./tools/Build-PluginSample.ps1 -Development -OutputDirectory <不存在的输出目录>
```

输出包含开发示例归档和本地 NuGet SDK。导入、信任与使用遵循正常插件流程，不自动安装到用户目录。

## 取消与数据一致性

应用服务在调试命令锁内读取已有快照，不增加 MI 查询。每个视图具有请求版本，恢复运行或工作区撤销先取消当前解释并清除面板；迟到响应不能恢复旧数据。单一后台循环与容量为 1 的最新请求队列合并连续刷新，80 ms 合并窗口，单次解释保护超时 5 秒。

API 3 原有字段保留，新增 `formatVersion: 1`、`revision`、`reason`。`revision` 是视图请求编号，不是设备时间。仅暂停快照可解释；错误状态、运行和工程不匹配时不返回上次寄存器数据。返回的面板拒绝未知字段和未声明动作，原始异常保留在视图及相关日志。

OpenOCD 的目标检查失败即使随后监听 GDB 端口，也会以 `DEBUG_TARGET_EXAMINE` 提前结束启动。错误保留原始 `Examination failed` / `Target not examined yet`，提示核对 SWD、供电与复位；不会追加下载、解锁或复位恢复。

## 离线验证

基础构建使用 `tools/Build.ps1 -Configuration Release`。独立验证命令：

```powershell
StudioX.DebugPluginValidation.exe <本次构建的 plugin-host 目录> <不存在的证据目录>
& '<构建目录>/MCU StudioX.exe' --preview-debug-plugins <独立输出目录> <F407 包归档> <开发示例插件归档>
```

在 PowerShell 中包含空格的 EXE 路径应使用调用运算符 `&` 和引号。界面入口自动使用输出目录内独立用户数据。离线 MI 来自固定 F407 模型，结果明确标为模拟，不代表实板数据。

硬件验证只有显式 `--hardware` 或 `--hardware-reset` 模式才会连接所选 F407/ST-Link；后者必须另有本轮复位连接授权，要求 NRST，并在所有安全配置完成后才初始化和复位暂停，等待 OpenOCD 明确确认再启动 GDB。普通 IDE 附加不自动选择此模式。它复制已有工程到新证据目录，实际编译，使用生产调试准备与板上映像一致性校验；校验失败停止，不下载固件。连接前必须确认本轮授权、型号和探针占用，不能将此模式用作常规自动测试。

低速大映像校验的 MI 等待上限为 2 分钟，普通命令保持 20 秒；GDB 远程包等待为 60 秒，均可取消，不放宽固件一致性要求。

2026-10-02 实机检查：所提供的探索者 V3.4/V3.5 原理图标注 STM32F407ZGT6 和 PA13/SWDIO、PA14/SWCLK。连接 ST-Link V2J46S7，带复位连接识别 Cortex-M4，读得 ID `0x10076413`、Flash 容量 `0x0400` KiB（1024 KiB）；退出恢复已由 OpenOCD 确认 running。LVGL 工程副本实际编译成功，但固件校验出现 checksum mismatch，后续传输超时，没有确认 ELF 与板上程序匹配，因此不计入源码单步或插件实机验收。全程未下载或擦除固件。

本轮证据保存于 `artifacts/debug-plugin-ecosystem-20261002`：服务结果、WPF 界面结果、原理图提取/渲染、隔离工程构建结果和 OpenOCD/GDB 原始硬件日志。
