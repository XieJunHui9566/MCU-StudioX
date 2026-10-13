# 外观工作室与 IDE 本体改动报告

2026-10-13。首阶段在 `0.2.6.13` 开发外观功能，随后按用户要求升级、打包并安装 `0.2.6.14`；首版外观插件为 `1.0.0`。字体乱码修复后，继续开发 `1.1.0` 氛围环境主题，IDE 产品版本不再升级。本次按用户要求发布对应源码、Windows 轻量安装包与独立插件到 GitHub，发行校验绑定同一个干净提交；外观开发与发布不操作硬件。

## 可用功能

外观工作室是独立 `.studioxplugin`，在欢迎页即可使用。从插件管理页导入并显式启用，点击左侧“外观”入口。内置深色/浅色和六组插件配色：深海蓝、森林绿、暮色紫、石墨灰、清爽白、暖纸色。

可设置界面字体/字号（11–18 px）、代码字体/字号（10–28 px）及代码粗体/斜体。字体列表来自本机已安装字体，不安装或下载字体。配色与字体先在面板内预览，点击“应用外观”才更新工作台并保存偏好。“恢复默认配色与字体”先重置选择，仍需点击应用。

字体列表按真实字形覆盖过滤符号/图标字体；既检查 Symbol 标记，也检查常用字母、数字和文件名字符，防止未标记为 Symbol 的 `AmdtSymbols` 把工程名显示成图形。保存前再次校验；旧配置含此类字体或已缺失字体时，启动恢复可读界面/代码字体并保存，保留字号、粗斜体、配色和壁纸。

### 氛围环境主题

日奈紫夜、月白、暮光三套环境使用同一张空校园紫夜 PNG，配套不同语义颜色。按用户后续要求，人物插画与樱花场景已从插件排除。素材由内置 imagegen 工具生成，不是官方游戏素材；提示词与来源说明随插件归档保存。

外观目录格式 1 仍支持原有纯色主题；格式 2 显式增加 `artwork`（包内 `assets/*.png` 相对资源名、SHA-256、强度），不允许任意 XAML、远程地址、字体文件或视频。应用服务核对当前已启用插件的完整哈希索引，最多读取 16 MiB PNG，并检查签名、IHDR、尺寸与像素上限；宿主实际解码成功后才发布主题偏好。

“显示氛围背景”和 0–100% 强度只影响独立主题图层。无壁纸时显示完整环境，编辑面板使用更实的底色保证文字可读；有壁纸时按设定强度的 30% 淡化叠加，仍尊重不透明工作区。切换主题不改写 `appearance.json`、不重开媒体、不重设播放位置。

环境副本以内容哈希命名，独立保存于用户 `theme-artwork` 目录，主题 JSON 只记录相对资源名和强度。重启、停用或卸载插件后仍可恢复；资源缺失/损坏时保留配色并记录原始诊断，不阻塞 IDE 启动。

此扩展涉及 IDE 本体：`PluginAppearanceArtwork` 契约、主题模型、目录校验、`ThemeArtworkService`、插件资源读取服务、宿主环境图层与预览/保存控件。`AppearanceService` 和媒体播放回调未修改；`MainWindow.Appearance.cs` 只增加主题环境透明表面的判断与强度刷新。

设置持久化在独立用户数据目录，工程源码不改变。插件禁用或卸载后已保存外观仍可恢复。工作台默认继承的界面文字使用所选字体；有独立用途的等宽日志、图标及显式字号仍保留自身样式。

## IDE 本体源码改动

本轮**涉及 IDE 本体源码修改**。此前插件只能提供一般声明式面板，不能通过宿主设置全局主题或界面字体；因此独立插件包需要以下通用宿主能力，不绑定芯片或插件 ID。

| 文件/模块 | 变更及原因 |
| --- | --- |
| `Extensions.Abstractions/PluginAppearanceDefinition.cs`、`PluginAppearanceTheme.cs`、`PluginAppearanceArtwork.cs` | 版本化外观目录 DTO，格式 2 加入包内 PNG 名称、哈希及强度，不包含 WPF 或执行代码 |
| `Application/Plugins/PluginAppearanceCatalog.cs`、`PluginContributionValidator.cs` | 接受应用级 `appearance` 控件，校验数量、字段、主题 ID 和八个颜色；工程插件及任意 XAML、字体/媒体路径拒绝 |
| `Application/TypographySettings.cs`、`TypographyService.cs` | 新增独立界面字体偏好，保存 `typography.json`，只接受字体名称与有效字号 |
| `Application/ThemeDefinition.cs`、`ThemeService.cs`、`ThemeArtwork.cs`、`ThemeArtworkService.cs`、`Plugins/PluginManagerService.Appearance.cs` | 校验、读取和保存独立主题环境资源，保持壁纸与主题分开持久化 |
| `Application/WorkbenchStyle.cs`、`WorkbenchStyleService.cs`、`WorkbenchService.cs` | 组合主题、界面字体与编辑器字体保存；保存失败回退已写偏好并保留原始诊断，不接触背景服务 |
| `Application/EditorSettings.cs`、`EditorSettingsService.cs` | 编辑器增加兼容旧配置的粗斜体字段；拒绝字体路径/URI |
| `Desktop/PluginPanelRenderer.cs`、`MainWindow.Plugins.cs` | 将外观目录交给宿主控件渲染，复用活动入口；面板刷新保留未应用输入 |
| `Desktop/PluginAppearanceView.cs`、`MainWindow.PluginAppearance.cs` | 宿主负责字体列表、局部预览、显式应用与会话资格校验；退出等待保存，迟到操作不可从已禁用插件应用 |
| `Desktop/App.xaml`、`MainWindow.xaml.cs` | 界面字体改用动态资源；启动恢复界面字体 |
| `Desktop/MainWindow.Editor.cs`、`AppearanceWindow.xaml.cs` | 应用编辑器粗斜体，原外观设置对话框保存时保留这两个字段 |
| `Desktop/TextFontCatalog.cs` | 根据本机字体元数据、Symbol 标记和实际字形覆盖筛选文本字体，过滤符号字体并修复已保存的不可读字体；原始元数据读取异常保留到诊断日志 |
| `Desktop/MainWindow.xaml`、`MainWindow.ThemeArtwork.cs`、`MainWindow.Appearance.cs` | 增加不接收点击的独立氛围层，按是否启用壁纸调整插画强度与可读表面，不改变媒体播放回调 |
| `Desktop/App.xaml.cs`、`MainWindow.AppearancePluginPreview.cs` | 增加隔离 UI 验证入口与真实视频播放检查，仅测试模式调用 |
| `StudioX.slnx` | 加入独立插件与外观验证工程 |

独立插件代码与清单位于 `examples/StudioX.AppearancePlugin`；打包脚本为 `tools/Build-AppearancePlugin.ps1`。用户插件仍在独立宿主执行，Desktop 不加载插件程序集。主题属于受校验数据，禁止任意 XAML。

**旧安装版兼容性：**已有相同产品版本的安装器不一定包含新增宿主能力；旧宿主会拒绝 `appearance` 控件。此包必须与本轮源码构建一起使用，仅导入插件到旧安装版不能实现功能。未以修改版本号来掩盖该差异。

## 动态壁纸保留方式

外观应用不调用 `ApplyBackground`，不重新设置 MediaElement.Source、不关闭/重开视频、不重置视频播放位置。`AppearanceService.cs` 和媒体播放方法未修改；`MainWindow.Appearance.cs` 仅补充主题环境的强度与透明表面刷新。继续尊重不透明工作区、透明度、调暗、模糊、适配、最小化和失焦暂停设置。

持久化分开：配色保存在 `preferences.json`/`themes`，界面字体在 `typography.json`，代码字体在 `editor.json`，动态壁纸继续使用 `appearance.json`/`backgrounds`。已保存主题复制在用户数据中，不依赖启用状态或插件安装目录。

## 验证与限制

基础构建使用 `tools/Build.ps1`。外观专项入口为 `tools/StudioX.AppearanceValidation`，包含目录/路径/字段边界、持久化、取消与保存失败回退、壁纸配置和资源字节保留。

真实 UI 使用 `--preview-appearance-plugin <独立输出目录> <插件归档> <本地视频>`：安装/信任、独立宿主、真实 WPF 字体与配色、媒体打开次数和播放推进、连续切换深浅主题、最小化暂停/恢复、禁用清理及迟到操作拒绝。本机视频样本来自已有 Microsoft Office 安装资源，不联网获取，也不随插件分发。

维护回归声明 `editor` 并使用已有语言运行时。首版证据位于 `.artifacts/appearance-plugins-20261013/`，字体修复位于 `.artifacts/appearance-font-fix-20261013/`，氛围扩展位于 `.artifacts/anime-themes-20261013/`。最终通过状态、检查数量和归档哈希以对应交付报告为准，文档中的验证入口不代表任意未来源码自动通过。

本轮外观插件针对 Windows WPF。Linux 版本暂不支持，已有 Avalonia 预览源码不在此次发行验收范围内。界面字体尺寸有边界，但未覆盖所有第三方字体、屏幕缩放和视频解码器组合；存储回退覆盖进程内失败，不承诺突然断电期间多个偏好文件的跨文件原子事务。
