# Keil5 工程移植插件 0.1.6

使用 MCU StudioX .NET 插件接口，需要同时支持应用级插件会话和 `projectLink` 工程入口的宿主。旧 0.2.6LTS 安装版不支持这些能力；请先应用宿主修复。优先支持已安装 StudioX 格式 1 器件包中的 STM32 ARM32 型号。移植对象是现有 Keil5 工程：原工程只读，在指定位置保留其源码、资源和目录结构，仅补充 StudioX / GCC 构建配置，不生成模板应用。

## 安装和使用

1. 在修复后的 IDE 插件管理中导入 `studiox.keil-importer-0.1.6.studioxplugin`，检查说明后信任并启用。更新会重新要求信任新包内容。插件版本独立于 IDE 产品版本，递增用于区分新版包内容。
2. 在欢迎页点击左侧“Keil5 移植”入口，无需打开工程。切换或关闭工程会保留移植表单和预览；停用、更新插件或退出 IDE 时结束会话。
3. 点击“浏览 Keil 工程…”选择 `.uvprojx` / ARM `.uvproj`，点击“浏览源码目录…”选择包含 MDK、Src、Drivers 等依赖的根目录，加载并明确选择 Keil Target。
4. 搜索已安装型号，明确选择与 Keil Device 一致的 MCU 和包版本，选择与原库匹配的构建支持配置。此选择只提供构建选项、器件宏和 GNU 支持，不复制 HAL/SPL/RTOS 应用模板。插件不会根据文件名猜测器件。
5. 填写移植后的工程名，点击“浏览输出位置…”选择已有目录，查看完整目标路径、保留的原相对路径、兼容性提示及生成的 CMakeLists.txt。
6. 点击“4. 预览迁移”，预览通过后显示确认勾选框。勾选后点击“5. 移植并验证编译”。误点未勾选的移植按钮会保留有效预览，可直接补勾选继续；修改字段会明确指出变化项并要求重新预览。插件使用当前 IDE 的受管理工具链，展示实际编译结果。
7. 点击结果区的“打开移植工程”，由 IDE 按正常流程处理当前未保存内容并打开原目录中的入口源码。移植时的原始日志进入构建输出，可定位的错误进入问题列表并标为“移植编译”，双击跳到源码。编译失败或副本发布后取消编译，仍可打开副本继续开发；发布前取消不会留下目标工程。生成副本后不会自动切换当前工作区。

路径支持中文和空格。所有路径项都有浏览按钮，使用插件自己的 STA 线程打开 [Windows Common Item Dialog](https://learn.microsoft.com/en-us/windows/win32/shell/common-file-dialog)，选择后自动回填；取消保留当前内容。仍可粘贴路径。工程选择窗口只显示 `.uvprojx/.uvproj`，目录窗口选择已有文件夹，CLI 窗口只显示 `StudioX.Cli.exe`。

已安装包目录默认使用 `%LOCALAPPDATA%/MCUStudioX/packs`；CLI 默认使用插件宿主相邻的 `runtime/mcp-host/StudioX.Cli.exe`，通常无需更改。选择窗口没有复制、创建或编译副作用，改变路径会清除旧预览；确认移植仍使用原有流程。绝对路径只用于本次操作，不写入工程配置和工具锁。无需修改 IDE 源码或引入 WPF / WinForms 依赖。

## 迁移规则

- 原工程文件按原相对路径复制到移植目录，保留 `.uvprojx/.uvproj`、源码、头文件、`.ioc`、图片及二进制资源；跳过 `.git`、Objects/Listings、build/Debug/Release 等构建与缓存目录。编译列表只使用所选 Target，保留宏和 IncludeInBuild 排除项，不执行 Keil 构建事件。
- 不调用 CLI 的 `create`，不生成模板 main，也不添加 `keil/` 或 `template-reference/` 包装目录。按公开格式 1 补充 `.studiox/project.json`、根 CMakeLists.txt，以及 `device/` 中必要的 GNU 启动、运行库、链接脚本、构建规则和器件元数据；已安装 CLI 用于核对工程身份并进行实际编译。
- CMake 的 `target_sources`、`target_include_directories` 和 `target_compile_definitions` 自动来自选定 Keil Target，无需手工重新登记源文件、头文件路径或宏。转换后调用 CLI 的 `build`，由 IDE 锁定并解析 `runtime/toolsets` 中的现有开发环境；不修改 PATH、不下载缺失 SDK。
- 编译结果、退出码、日志 SHA-256 与源码快照写入 `.studiox/keil-build.json`，stdout/stderr 原始诊断保存在 `.studiox/keil-build.log`。编译失败或取消保留已发布副本及已收到的两路工具输出；输出超限立即结束该进程树。打开时核对源码和日志哈希，变化的源码不显示旧错误标记，缺失或损坏的编译记录不阻止打开工程修复。
- 原 STM32 启动汇编保留在副本中供核对，构建使用所选包的 GCC 启动文件。已有 `system_stm32*.c` 优先保留；缺少时明确警告并使用包的系统初始化。须人工核对自定义复位初始化、向量、时钟、堆栈和运行库差异。
- 首次打开显示原目录中的 main。器件宏从明确选定的构建支持配置补充时会列入预览，不根据 Flash 容量猜测宏；不注入模板应用的晶振和向量表默认宏，以免覆盖原配置头文件。`.studiox/keil-import.json` 记录所选身份、排除项、警告和每个原文件的 SHA-256。应用和厂商库版本保留原工程内容。
- 识别到 CMSIS V1.30 的已验证 STREX 写法时，逐项预览 `"=r"` → `"=&r"` 的 GCC early-clobber 修正。只修改新工程副本并分别记录原始/生成 SHA-256，保留文件编码和换行。依据为 [GCC 官方扩展汇编约束说明](https://gcc.gnu.org/onlinedocs/gcc/Extended-Asm.html)；未能唯一匹配的变体需要人工处理。
- 不覆盖已有目标，不允许源/目标目录重叠，不接受链接或网络目录。原工程已有根 CMakeLists.txt、.studiox/ 或 device/ 时明确报告冲突，不静默覆盖。预览后修改表单、源码、器件包或 CLI 必须重新预览。复制成功并核验后才发布最终目录；失败或取消清理本次专用临时目录。

## 明确限制

本版不自动处理自定义 scatter/bootloader 内存布局、Keil 专有二进制库、复杂宏、环境变量路径、逐文件/逐组编译覆盖、非启动 ARMASM、Keil 专用嵌入汇编、构建事件和 RTE 中间件。遇到这些配置会阻止创建并给出操作提示；无需改动原工程，可先整理一份迁移用副本。不会下载 SDK、安装全局运行环境或连接硬件。

插件以当前用户权限访问文件系统，进程隔离不是权限沙箱。新工程目录写入由用户在插件预览中明确确认；不调用当前绑定工程的写入工具，也不以文件系统访问绕过宿主拒绝的写操作。

移植完成和编译通过分别报告；只有 CLI 成功退出且生成 ELF 才报告自动编译通过。编译不代表实板验收。[Keil 汇编器说明](https://www.keil.com/support/man/docs/uv4/uv4_dg_adsas.asp)和 [Arm 官方编译器迁移说明](https://developer.arm.com/community/arm-community-blogs/b/tools-software-ides-blog/posts/migrating-a-project-from-gcc-to-arm-toolchain-for-embedded)说明了汇编语法、编译器和链接格式的差异。

## 构建

仅需要 .NET 10 SDK 和已安装 IDE，不引用 IDE 私有服务程序集：

```powershell
./Build.ps1 -InstallDirectory '<IDE 安装目录>' -OutputDirectory '<不存在的插件输出目录>'
```

生成独立 `.studioxplugin`、校验文件和本说明；中间构建目录在打包后清理。插件只引用安装版 `StudioX.Extensions.Abstractions.dll`，包内不附带契约 DLL 或 IDE 源码。

## 回归验证

`tools/Test-Regression.ps1` 默认包含 Keil 工作流检查：无 SDK 的真实进程失败/取消/输出超限，以及独立宿主、欢迎页、工程切换、停用/更新、打开工程、错误定位和过期诊断的 WPF 检查。不会连接设备。

传入 `-KeilValidationInputs <JSON>` 可加入已有 F407 HAL / F103 SPL 工程的真实迁移编译。JSON 明确提供 `cli`、`packs`、`toolsets`、`f407Project`、`f103Project` 五个本机路径；输入工程保持只读，输出写入独立验收目录。未提供这些输入时，报告明确标为未请求真实编译。
