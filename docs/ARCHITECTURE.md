# MCU StudioX 架构

产品使用独立的 C# 核心与 Windows WPF 桌面。安装包包含运行时；芯片、工具、插件和主题是四种独立资产。具体功能的支持范围与硬件证据以对应模块文档为准，不能沿用早期脚手架阶段的限制或把离线验证当作实板验收。

产品面向开箱即用、新手友好、功能完整且可通过插件扩展的重型 IDE。器件系列由包和对应能力适配，共用编辑、工程、构建、连接与扩展基础设施。工具按 IDE 发行版和工程锁定版本启动，不写系统 PATH、不依赖全局 Node/Python 环境；缓存、语言索引和恢复草稿独立存入用户数据目录。

工程搜索和修改计划由 `Application.Editing.WorkspaceEditService` 生成并校验，Desktop 仅呈现预览并在编辑文档中应用可撤销修改。clangd 诊断按工程世代和文档版本关联，引用与重命名共享编辑快照。`EditorSessionStore` 负责原子草稿存储和实例租约；不会在恢复时写入源文件。详见 [工程编辑工作区](EDITOR_WORKSPACE.md)。

源码入口、资源所有权和退出顺序见 [职责导航](RESPONSIBILITIES.md)，统一排版与中文注释约定见 [源码风格](CODE_STYLE.md)。

## 模块

| 模块 | 职责 |
|---|---|
| StudioX.Foundation | 版本化数据、路径边界、原子存储、子进程生命周期 |
| StudioX.Packages | 新格式 Pack 完整性校验、安全导入、内容索引与仓库 |
| StudioX.Engine | 原生 C# 工程生成、构建执行、工具目录和版本锁定 |
| StudioX.Devices | 连接所有权、数据广播、发送串行化、模拟传输、记录格式 |
| StudioX.Extensions.Abstractions | 插件公开 SDK 与协议 DTO，不引用宿主实现 |
| StudioX.Extensions | 插件清单校验、目录、独立宿主客户端 |
| StudioX.PluginHost | 持久加载受用户信任的 .NET 插件，处理版本化双向协议与生命周期 |
| StudioX.Application | 组合工作台用例、文件服务、clangd/LSP 生命周期、主题和用户偏好 |
| StudioX.Desktop | WPF 工作台、命令与只读状态呈现 |

依赖方向：Desktop → Application → Engine / Devices / Extensions → Foundation；Engine → Packages。PluginHost 与插件通过 Abstractions 合同通信；插件不引用 Desktop。

## 独立原生核心

根据用户最新决定，本项目不要求兼容旧体系。所有核心模块使用 C#；不分发 Node，不复用旧 TypeScript 运行时，不迁移 PLC 工程。旧产品仅参考已验证的设计经验和芯片启动/链接参数。

Packages 完整校验新的 ZIP 芯片包，Engine 从明确选择构造纯 BuildPlan，再生成 CMake 工程并执行内置工具。Pack 导入先在自身 staging 中校验所有文件 SHA-256，再发布到用户仓库；同 ID/版本的不同内容拒绝覆盖。哈希只提供完整性，不等于作者签名。外部工具取消/超时终止整个进程树，原始诊断保留。

工程保存 `.studiox/project.json` 格式 1，工具锁独立保存于 `.studiox/toolchain.lock.json`。工具只按精确 ID/版本选择，不搜索 PATH，不将绝对安装路径写入工程。工具清单使用安装目录内相对路径，允许完整安装目录搬迁。

Engine 生成分层 CMake：根 `CMakeLists.txt` 交给用户维护，`device/CMakeLists.txt` 与 `device/platform.cmake` 管理固定 SDK、器件参数和产物规则。Application 将带生成器标识的内部配置视为只读，Desktop 显示原因；器件包内容不承担开发环境组件分发。布局和维护约定见 [工程分层](PROJECT_LAYOUT.md)。

## 设备与实用工具

工程版本管理：WorkbenchService 向 ProjectService 注入内置 Git 初始化服务，在新工程 staging 内创建独立仓库后再发布到目标目录；失败不留下半成品工程。Git 位于 `runtime/git`，与芯片包、编译器工具锁分离。ProjectTerminalService 管理每工程一个持久 CMD 会话，Foundation 的 Windows ConPTY 提供标准控制台交互与 Job 进程树回收，Application 的 ConsoleScreen 解释 VT 显示，Desktop 仅负责输入、着色、滚动和入口。后台持续排空输出，有界历史与版本缓存避免无限内存增长。见 [Git 与工程终端](GIT-TERMINAL.md)。

编辑工作区的代码提示由 Application/CodeIntelligence 管理内置 clangd 子进程、编译分析参数、工程源码索引、所有打开文档的同步和请求取消；Desktop 传递内存快照与位置，呈现补全、参数、悬停与声明/定义导航。Application 校验跳转目标，只读开放内置标准头文件；Desktop 维护标签和文本锚点导航历史。工程数据与语言缓存分离，不依赖 VS Code、Node 或全局 PATH。当前配色仍为本地词法规则，语言诊断界面和真实 CMake 编译数据库同步留待后续接入。

当前文件结构同样通过 Application 获取 clangd `documentSymbol` 语义声明；展开函数时再请求 `textDocument/ast` 读取参数和局部变量。Desktop 负责分组、筛选、展开和导航，使用后台请求、编辑防抖、取消及文档版本检查，避免把旧文件结果显示到新标签。

每个连接键只有一个会话；一份接收数据广播给多个订阅者。各订阅者有有界队列，慢消费者只影响自身且有丢帧计数。发送通过同一串行锁，观察者不能擅自占用传输。

工作台保留确定性的模拟设备，用于验证日志、数值曲线、数字状态波形共用会话。模拟时间/宿主接收时间明确区分。TCP/采集硬件仍为预留，未接入的传输不会标记为可用。采集文件为版本化 JSON Lines，可离线读取；高吞吐二进制采集、硬件时间同步留待后续。

文字串口现已接入：Devices 中的 SerialTransport 通过 System.IO.Ports 提供有界超时字节传输、参数与信号控制；Application/Serial 管理接收解码、ANSI 显示状态、原始历史、导出及连接清理。Desktop 的 SerialTerminalView 独立于工程，使用 AvalonEdit 呈现有颜色的只读终端；不直接访问端口。首包在建立观察者后开始读取，后台测试使用同一传输接口的可控数据源。功能范围与暂缓的实板界面验收见 [文字串口终端](SERIAL_TERMINAL.md)。

串口绘图作为独立工具接入 Application/SerialPlot 与 SerialPlotView，不与文字终端共享导航页。可配置分隔符的增量解析在后台运行，数值 / 时间曲线使用有界历史、按像素保峰值绘制及原生滚轮缩放路由；端口仍受 DeviceHub 单一所有者约束。格式、时钟语义、内存边界和验证范围见 [串口绘图](SERIAL-PLOT.md)。

## 插件与主题

插件清单格式为 1，API 1 保留明确的数据解码契约，API 2 开放命令、声明式面板和 Agent 工具。C# 使用公开 SDK；Python、Rust、C++ 通过插件目录内固定 EXE 和相同的双向 JSON 行协议接入。`.studioxplugin` 安装只校验文件，显式启用才信任当前版本的代码执行。用户安装与启用指纹保存在用户数据目录；内容更新撤销启用，避免授权继承到新代码。详见 [插件开发](PLUGINS.md)。

Application/Plugins 管理安装目录、贡献校验和每工程宿主。PluginWorkspaceBroker 将主机请求交给既有 MCP 应用工具，继续检查工程范围、文件哈希、脏缓冲区和逐次写入、构建、设备授权。串口、调试和下载仍由应用服务拥有；插件声明 HostTools 不能替代用户操作授权。Desktop 只渲染经校验的面板数据，不执行用户 XAML；编辑缓冲区通过应用接口读取与定位。

插件在独立进程运行，宿主负责取消、退出和原始诊断；进程隔离不构成 OS 权限沙箱，用户插件仍具有当前用户的文件和网络权限。签名、原生 UI 注入及操作系统沙箱没有开放，不能把哈希或 AssemblyLoadContext 当作安全边界。

主题接受 JSON 语义颜色，WPF 由应用控制的 ResourceDictionary 映射。用户偏好在 LocalAppData，主题不进入工程和构建指纹。工作台采用 CLion 风格的中性深灰布局，支持浅/深主题、配色导入、本地背景图片和静音循环视频。AppearanceService 管理媒体导入与设置，Desktop 负责渲染和播放生命周期；媒体不作为插件代码执行。详见 WORKBENCH_UI.md。停靠布局持久化、主题包市场留待后续。

## 基础架构验收目标

1. 不依赖 VS Code 的桌面程序可发布运行。
2. 导入按新格式制作的 AG32 `.mcupack`，完整验证，明确选择设备/模板后创建工程；旧格式明确拒绝。
3. 使用明确配置的内置工具编译工程，得到 ELF/BIN/HEX/MAP；调试不会隐式烧录。
4. 模拟连接只有一个实例，两个观察者收到同序列；慢消费者、取消和重复连接有测试。
5. 正式插件示例在独立进程处理数据；不兼容 API、错误入口和异常响应被拒绝。
6. 主题切换及重启恢复有效；无效主题不替换当前主题。

## 调试与能力边界

Application 管理调试状态与断点设置，Engine 管理 MI 协议、硬件进程和模拟传输，Desktop 提供寄存器、调用栈、变量、反汇编和 RTOS 窗口。离线演示明确标记模拟来源；硬件连接与固件写入分别授权。范围和证据见 [调试交互](DEBUGGING.md)及 [RTOS 调试](RTOS_DEBUGGING.md)。

各能力的可用状态由实际实现与验证证据决定。预留的 TCP、硬件时序采集和扩展市场不因界面入口或接口存在而宣称可用。
