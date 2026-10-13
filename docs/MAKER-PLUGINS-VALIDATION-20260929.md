# 创客插件验收 · 2026-09-29

本轮新增四个 API 2 插件，版本均为 1.0.0：`studiox.rgb-studio`、`studiox.melody-studio`、`studiox.pid-studio`、`studiox.debounce-studio`。源代码与清单位于 `examples/StudioX.MakerPlugins`，独立构建脚本为 `tools/Build-MakerPlugins.ps1`。

## 交付与独立性

- 归档：`artifacts/plugins/StudioX-Makers-1.0.0/`，每个约 51 KiB；合集 `artifacts/plugins/StudioX-Makers-1.0.0.zip`。
- 四个插件均已导入当前用户的 `MCUStudioX/plugins` 并启用；原来的四个实验室插件保持启用。
- 复用既有 CLI、插件宿主、声明式 WPF 渲染器和活动栏接口。没有改动 `src`、产品版本号或重编/替换 IDE。
- 既有本地 Desktop DLL 在任务前后均为 SHA-256 `E41C895BDD592A29D709443D67F101086D965CC0952F8A0DBFC9A8BBF570F9FB`。
- 每个归档仅携带创客插件 DLL、文档和清单，不依赖第一批插件的安装，不携带 IDE 核心或私有 SDK 运行时副本。

## 96 项计算、C 导出与真实宿主检查

测试项目：`tools/StudioX.MakerPluginsValidation`。结果：`artifacts/maker-plugins-20260929/checks3/result.json`，日志 `validation3.log`。

- RGB 六个标准色相边界、RGB565、GRB 顺序、亮度/指数、呼吸对称性与输入边界。
- 音符八度、升降同音、休止、节拍门控、累计舍入与非法格式。
- 两类消抖的预期事件时间、短脉冲、初始按下、量化与非法轨迹。
- PID 一阶/二阶对象与解析响应对照、离散 P 闭环解析递推、延迟网格、稳定与不可达判断、无设定值微分冲击、抗饱和与反向退饱和、固定噪声、微分滤波、扰动恢复。
- 使用本机已有 GCC 实际编译并运行导出的 C99：五组 PID 各 400 步，与 C# 对照 P/I/D 和输出，最大误差约 `1.37e-6`；两组消抖代码匹配预期物理电平事件；RGB/旋律的最大规模数组可编译并运行。全程不安装编译器或更改系统 PATH。
- 四个真实 `.studioxplugin` 归档导入、图标保留、独立宿主激活、Agent 调用；PID 分页状态、错误恢复、预设、基线、C/CSV 限长、5001 点历史分页、禁用终止宿主。

测试发现并修复了基线曲线与按钮控件 ID 重复，以及长仿真完整 JSON 超出既有宿主节点上限的问题。后者在插件中使用最多 1000 点的分页返回，保留完整数据，不修改宿主限制。

## 17 项既有 WPF 渲染器回归

测试项目：`tools/StudioX.MakerPluginsUiValidation`。结果：`artifacts/maker-plugins-20260929/ui1/result.json`。

直接引用已经构建好的 Desktop DLL 和真实应用资源，在独立测试进程内实例化其 `PluginPanelRenderer`，使用实际 WPF TextBox/ComboBox/Button 提交插件表单。没有在 IDE 内新增测试入口或插件 ID 分支。

覆盖调参、翻页、错误恢复、仿真、基线、试验条件差异提示、预设替换缓存值、导出以及其余三个插件的可见结果刷新。截图检查了深色、浅色、520px 窄窗口和滚动后的指标区：

- `ui1/pid-tuning.png`、`pid-response.png`、`pid-metrics.png`
- `ui1/pid-compact.png`、`pid-light.png`、`pid-export.png`
- `ui1/rgb.png`、`melody.png`、`debounce.png`

这些是宿主原有控件的进程内渲染结果，不是完整 IDE 的原生鼠标回放。Windows computer-use 可以读取窗口，但激活原生窗口连续返回 `failed to activate captured window`，因此没有宣称原生点击验收通过。

## 验证范围

本轮为离线数学、生成代码和插件集成验收，没有连接、下载或调试硬件。PID 模型参数不代表目标器件已辨识，稳定时间是有限观察窗口中的判定；具体公式和指标口径见插件 README。

普通安装版早期 0.2.5.3 尚未携带上轮新增的通用活动栏接口；插件面板仍可在其扩展页运行。本地 `artifacts/build/plugin-labs/bin/StudioX.Desktop/release_win-x64/MCU StudioX.exe` 包含该通用接口，打开工程后提供插件独立图标。本轮没有制作或替换 IDE 安装包。
