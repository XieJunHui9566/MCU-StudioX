# MCU StudioX 架构 / Foundation 0.1

本阶段建立独立 Windows 应用的开发脚手架。按用户最新要求，先完成结构与接口，只做基础编译检查，器件和端到端验证暂缓。正式发行时安装包包含运行时；芯片、工具、插件和主题是四种独立资产。

## 模块

| 模块 | 职责 |
|---|---|
| StudioX.Foundation | 版本化数据、路径边界、原子存储、子进程生命周期 |
| StudioX.Packages | 新格式 Pack 完整性校验、安全导入、内容索引与仓库 |
| StudioX.Engine | 原生 C# 工程生成、构建执行、工具目录和版本锁定 |
| StudioX.Devices | 连接所有权、数据广播、发送串行化、模拟传输、记录格式 |
| StudioX.Extensions.Abstractions | 插件公开 SDK 与协议 DTO，不引用宿主实现 |
| StudioX.Extensions | 插件清单校验、目录、独立宿主客户端 |
| StudioX.PluginHost | 加载受用户信任的 .NET 数据处理插件，标准输入输出通信 |
| StudioX.Application | 组合工作台用例、文件服务、clangd/LSP 生命周期、主题和用户偏好 |
| StudioX.Desktop | WPF 工作台、命令与只读状态呈现 |

依赖方向：Desktop → Application → Engine / Devices / Extensions → Foundation；Engine → Packages。PluginHost 与插件通过 Abstractions 合同通信；插件不引用 Desktop。

## 独立原生核心

根据用户最新决定，本项目不要求兼容旧体系。所有核心模块使用 C#；不分发 Node，不复用旧 TypeScript 运行时，不迁移 PLC 工程。旧产品仅参考已验证的设计经验和芯片启动/链接参数。

Packages 完整校验新的 ZIP 芯片包，Engine 从明确选择构造纯 BuildPlan，再生成 CMake 工程并执行内置工具。Pack 导入先在自身 staging 中校验所有文件 SHA-256，再发布到用户仓库；同 ID/版本的不同内容拒绝覆盖。哈希只提供完整性，不等于作者签名。外部工具取消/超时终止整个进程树，原始诊断保留。

工程保存 `.studiox/project.json` 格式 1，工具锁独立保存于 `.studiox/toolchain.lock.json`。工具只按精确 ID/版本选择，不搜索 PATH，不将绝对安装路径写入工程。工具清单使用安装目录内相对路径，允许完整安装目录搬迁。

Engine 生成分层 CMake：根 `CMakeLists.txt` 交给用户维护，`device/CMakeLists.txt` 与 `device/platform.cmake` 管理固定 SDK、器件参数和产物规则。Application 将带生成器标识的内部配置视为只读，Desktop 显示原因；器件包内容不承担工具链分发。布局和维护约定见 [工程分层](PROJECT_LAYOUT.md)。

## 设备与实用工具

工程版本管理：WorkbenchService 向 ProjectService 注入内置 Git 初始化服务，在新工程 staging 内创建独立仓库后再发布到目标目录；失败不留下半成品工程。Git 位于 `runtime/git`，与芯片包、编译器工具锁分离。ProjectTerminalService 管理每工程一个持久 CMD 会话，Foundation 的 Windows ConPTY 提供标准控制台交互与 Job 进程树回收，Application 的 ConsoleScreen 解释 VT 显示，Desktop 仅负责输入、着色、滚动和入口。后台持续排空输出，有界历史与版本缓存避免无限内存增长。见 [Git 与工程终端](GIT-TERMINAL.md)。

编辑工作区的代码提示由 Application/CodeIntelligence 管理内置 clangd 子进程、编译分析参数、工程源码索引、所有打开文档的同步和请求取消；Desktop 传递内存快照与位置，呈现补全、参数、悬停与声明/定义导航。Application 校验跳转目标，只读开放内置标准头文件；Desktop 维护标签和文本锚点导航历史。工程数据与语言缓存分离，不依赖 VS Code、Node 或全局 PATH。当前配色仍为本地词法规则，语言诊断界面和真实 CMake 编译数据库同步留待后续接入。

当前文件结构同样通过 Application 获取 clangd `documentSymbol` 语义声明；展开函数时再请求 `textDocument/ast` 读取参数和局部变量。Desktop 负责分组、筛选、展开和导航，使用后台请求、编辑防抖、取消及文档版本检查，避免把旧文件结果显示到新标签。

每个连接键只有一个会话；一份接收数据广播给多个订阅者。各订阅者有有界队列，慢消费者只影响自身且有丢帧计数。发送通过同一串行锁，观察者不能擅自占用传输。

工作台保留确定性的模拟设备，用于验证日志、数值曲线、数字状态波形共用会话。模拟时间/宿主接收时间明确区分。TCP/采集硬件仍为预留，未接入的传输不会标记为可用。采集文件为版本化 JSON Lines，可离线读取；高吞吐二进制采集、硬件时间同步留待后续。

文字串口现已接入：Devices 中的 SerialTransport 通过 System.IO.Ports 提供有界超时字节传输、参数与信号控制；Application/Serial 管理接收解码、ANSI 显示状态、原始历史、导出及连接清理。Desktop 的 SerialTerminalView 独立于工程，使用 AvalonEdit 呈现有颜色的只读终端；不直接访问端口。首包在建立观察者后开始读取，后台测试使用同一传输接口的可控数据源。功能范围与暂缓的实板界面验收见 [文字串口终端](SERIAL_TERMINAL.md)。

串口绘图作为独立工具接入 Application/SerialPlot 与 SerialPlotView，不与文字终端共享导航页。可配置分隔符的增量解析在后台运行，数值 / 时间曲线使用有界历史、按像素保峰值绘制及原生滚轮缩放路由；端口仍受 DeviceHub 单一所有者约束。格式、时钟语义、内存边界和验证范围见 [串口绘图](SERIAL-PLOT.md)。

## 插件与主题

插件清单声明 formatVersion、apiVersion、ID、版本、程序集、入口类型及能力。首版只开放数据解码器，插件在独立宿主中运行，提供正式示例。宿主负责超时与崩溃恢复；这不是防恶意代码的 OS 沙箱，用户代码以当前用户权限运行。后续受信原生 UI 扩展、签名、权限沙箱需独立设计，不能把 AssemblyLoadContext 当作安全边界。

主题接受 JSON 语义颜色，WPF 由应用控制的 ResourceDictionary 映射。用户偏好在 LocalAppData，主题不进入工程和构建指纹。工作台采用 CLion 风格的中性深灰布局，支持浅/深主题、配色导入、本地背景图片和静音循环视频。AppearanceService 管理媒体导入与设置，Desktop 负责渲染和播放生命周期；媒体不作为插件代码执行。详见 WORKBENCH_UI.md。停靠布局持久化、主题包市场留待后续。

## 后续验证目标（本轮不执行）

1. 不依赖 VS Code 的桌面程序可发布运行。
2. 导入按新格式制作的 AG32 `.mcupack`，完整验证，明确选择设备/模板后创建工程；旧格式明确拒绝。
3. 使用显式准备的内置工具编译真实 AG32 工程，得到 ELF/BIN/HEX/MAP；本阶段不自动烧录。
4. 模拟连接只有一个实例，两个观察者收到同序列；慢消费者、取消和重复连接有测试。
5. 正式插件示例在独立进程处理数据；不兼容 API、错误入口和异常响应被拒绝。
6. 主题切换及重启恢复有效；无效主题不替换当前主题。

## 后续顺序

STM32F407/ST-Link 调试交互的离线阶段已接入：Application 管理调试状态与断点设置，Engine 管理 MI 协议与模拟传输，Desktop 提供寄存器、调用栈、变量和断点窗口。当前没有硬件进程入口；范围和验证见 [调试交互](DEBUGGING.md)。

真实串口/TCP → 记录回放 UI → 协议主从模拟 → 硬件时序采集 → 编辑器诊断与编译数据库同步 → OpenOCD/GDB 调试 → 扩展市场和更新。没有完成这些功能前不对外宣称完整 IDE。
