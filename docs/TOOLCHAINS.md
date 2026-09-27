# 内置工具链

CH32V203 的 11 个型号使用现有 `wch.riscv/1.0.0`，独立包制作、CCT6 ISA 区别和验证命令见 [V203 软件适配](CH32V203.md)。

普通用户使用完整的 StudioX 发行目录，通过器件包选择精确工具集，不配置编译器路径。工具链属于 IDE；SDK、启动代码、寄存器定义、链接脚本与模板属于 `.mcupack`。

## 当前组合

| 工具集 / 版本 | 编译器标识 | 组件 |
|---|---|---|
| agm.agrv / 1.0.0 | agrv-gcc-11.1.0 | AGM GCC 11.1.0，AGM OpenOCD 0.12.0+dev-04519-ga93c217e2-dirty |
| arm.gnu / 1.0.0 | arm-gnu-15.2.rel1 | Arm GNU 15.2.Rel1 / GCC 15.2.1，xPack OpenOCD 0.12.0-7 |
| riscv.xpack / 1.0.0 | xpack-riscv-gcc-15.2.0 | xPack RISC-V GCC 15.2.0，xPack OpenOCD 0.12.0-7 |
| wch.riscv / 1.0.0 | wch-gcc-12.2.0-v1.4 | MounRiver 沁恒 GCC 12.2.0 v1.4，WCH OpenOCD 0.11.0+dev（2026-08-25） |
| stc.sdcc / 1.0.0 | sdcc-4.5.0-15242 | SDCC 4.5.0 MCS-51，CMake 4.4.0，Ninja 1.10.2；不含下载或调试工具 |
| pc.mingw / 1.0.0 | mingw-gcc-13.1.0 | Windows x64 原生 GCC / G++ 13.1.0、binutils、MinGW-w64 11；供 LVGL PC 预览使用 |
| espressif.idf / 5.5.4 | esp-idf | ESP-IDF 5.5.4，Espressif GCC esp-14.2.0_20260121，Python 3.11.2，CMake 3.30.2，Ninja 1.12.1 |
| espressif.esp8266-rtos / 3.4.0 | esp8266-rtos | ESP8266 RTOS SDK v3.4，Xtensa LX106 GCC 8.4.0-esp-2020r3，Python 3.8.10；独立于 ESP-IDF |

前四套 GCC 工具集包含 CMake 4.4.0、Ninja 1.10.2 及匹配的 GDB、binutils、运行库、标准库、头文件、OpenOCD 脚本。STC 工具集包含 SDCC、CMake、Ninja 及 SDCC 的 MCS-51 标准头文件和库，不包含 OpenOCD。AG32 使用厂商专用版本，不能直接替换为通用 RISC-V GCC/OpenOCD。

`pc.mingw` 明确标记 `purpose: windows-native`，不能用来生成 MCU 固件。PC 预览默认使用此内置工具集，无需用户安装 MinGW 或选择开发者机器路径。它复用已有 CMake/Ninja，保留 C/C++、LTO、Win32 头文件/导入库、启动文件和所需 DLL；剔除 GDB/Python、Fortran、文档、翻译与重复驱动入口。PC 工具和 MCU 工具分别解析和校验。

AG32 可选的 Verilog 逻辑模式另需用户安装 **Quartus II Full 与 AGM Supra**，用于 Verilog 转换和逻辑镜像生成；它们不包含在上述 `agm.agrv` MCU 工具集中。StudioX 目前提供工具位置与工程检查、构建和下载指引，实际综合及逻辑下载按厂商流程执行，见 [AG32 逻辑模式](AG32_LOGIC_MODE.md)。

## 开发者准备与发行

`tools/Prepare-ToolRuntime.ps1` 只读取明确提供的本地工具目录，不下载厂商 SDK。它验证工具版本、保留完整运行组件和许可资料，并生成逐文件 SHA-256 清单及来源记录。目标目录必须不存在，已发行组合不能在相同版本下覆盖。

```powershell
.\tools\Prepare-ToolRuntime.ps1 `
  -AgRvDirectory '<AgRV GCC 根目录>' `
  -AgRvOpenOcdDirectory '<AGM OpenOCD 根目录>' `
  -ArmGccDirectory '<Arm GNU GCC 根目录>' `
  -RiscVGccDirectory '<xPack RISC-V GCC 根目录>' `
  -OpenOcdDirectory '<xPack OpenOCD 根目录>' `
  -CMakeDirectory '<CMake 根目录>' `
  -NinjaExecutable '<ninja.exe 的完整路径>'

.\tools\Build.ps1
.\tools\Publish.ps1
```

准备输出默认是 `artifacts/tool-runtime`。Desktop 的 Content 项统一复制到开发输出与发布输出中的 `runtime/toolsets`。发布脚本生成 Windows x64 自包含应用，不要求用户安装 .NET；缺少工具集或 clangd 时拒绝发布。全量运行组件会占用数 GB，安装器压缩与共享组件去重属于后续产品化工作。

工具资料见 `runtime/THIRD-PARTY-NOTICES.txt`、各工具集的 `provenance.json` 和 `licenses/`。当前是本地开发预览资源；正式对外发行还应归档供应商对应源码、构建修改和再分发资料。

沁恒资源单独追加，不覆盖上述工具集：`tools/Prepare-WchToolRuntime.ps1 -WchComponentsDirectory '<MounRiver 的 WCH 组件目录>'`。
默认读取 `Toolchain/RISC-V Embedded GCC12` 和 `OpenOCD/OpenOCD`，复用已准备的 Arm 工具集中 CMake、Ninja 与许可证；输出 `artifacts/tool-runtime/toolsets/wch.riscv/1.0.0`。

STC 8 位资源也单独追加，不覆盖已有工具集：

```powershell
.\tools\Prepare-SdccToolRuntime.ps1 -SdccDirectory '<本机 SDCC 安装目录>'
```

输出为 `artifacts/tool-runtime/toolsets/stc.sdcc/1.0.0`。脚本先核对 SDCC 4.5.0 #15242、CMake 4.4.0、Ninja 1.10.2 及所需程序，再复制 SDCC 的 `bin/doc/include/lib` 和原始许可证，复用 `arm.gnu/1.0.0` 中的 CMake/Ninja，最后生成来源记录和逐文件 SHA-256 清单。SDCC 的 `non-free` 目录是与 STC 无关的 Microchip PIC 资料，不进入此工具集。既有输出目录不会被覆盖。该工具集仅用于构建，不提供 STC 下载或调试。

Windows PC 工具单独准备：

```powershell
.\tools\Prepare-PcToolRuntime.ps1 -MinGWDirectory '<本机 MinGW GCC 13.1.0 根目录>'
```

固定输出 `artifacts/tool-runtime/toolsets/pc.mingw/1.0.0`。脚本先检查 GCC 版本、`x86_64-w64-mingw32` 目标、原始构建记录、每个保留 PE 的 DLL 依赖闭包；再以仅包含准备目录和 Windows 系统目录的 PATH 编译并运行 C17/C++17 + LTO 探针，验证标准库、异常和文件路径功能。编译器、头文件、库的宿主环境覆盖变量均清除。清单包含全目录 SHA-256 和原始来源指纹；重复执行只校验并复用同一内容，不覆盖不同内容的既有版本，不创建时间戳历史目录。发行脚本要求此包存在并由 Desktop 的现有 Content 规则复制到 `runtime/toolsets`，因此便携版和安装包都包含 PC 编译能力。许可与来源见 [PC 工具链说明](../licenses/PC-Toolchain-NOTICE.md)。

CH32V307 独立包：`python tools/New-Ch32V307Pack.py --sdk '<CH32V307/NoneOS>' --output artifacts/packs/CH32V307-0.1.1`。
脚本先校验本地官方 ZIP 的固定哈希，再生成格式 1 包；输出目录必须不存在。发布时放到 `device-packs/WCH/`。
提供 VCT6/RCT6/WCU6 标准库模板，默认外部 8 MHz → 144 MHz，256 KiB Flash / 64 KiB RAM。
WCH 扩展 ISA 只交给沁恒编译器，clangd 使用标准 ISA 分析并读取对应 newlib 头文件。
0.1.1 包开放 WCH-Link(E) SDI 下载，显式启用按页编程并核对芯片与内存划分；速度支持 400/4000/6000 kHz，当前仅支持单台连接。实机调试尚未开放。

## 构建行为

### Espressif 共享 SDK

两套 SDK 工具集均锁定 `purpose`、SDK 版本、目标编译器、资源相对目录和逐文件 SHA-256。ESP32-WROOM-32/P4/S3/C3/C5/C6 共用 `espressif.idf/5.5.4`；ESP8266 使用 `espressif.esp8266-rtos/3.4.0`，不会混用 IDF 5.5.4 的目标列表。开发构建采用硬链接复用只读资源，同一卷上的重复文件不再次占用完整空间；工程仅保存组件源码、配置和构建产物。

F7 路由到 SDK `idf.py reconfigure/build`，实际 `sdkconfig`、组件依赖和链接布局由原生 SDK 处理。保留应用 ELF/MAP/BIN、bootloader、partition table、`flasher_args.json`、原始日志和编译凭据；下载按 SDK 布局校验每个映像。生成工程不使用通用 MCU 链接脚本。优化与调试开关按当前工程设置作用于原生应用编译，SDK 自身配置仍可在 `sdkconfig` 中审查。

语言服务读取每个源文件的原生 `-D`、`-I` 与语言标准，只在用户缓存中转换 LLVM 不能解析的 GCC 参数；原始编译数据库不修改。RISC-V 使用标准 32 位目标，Xtensa 使用通用 32 位 C/C++ 声明解析，不验证 Xtensa ABI。首次打开未配置工程会明确提示先配置或编译生成 SDK 头文件，界面不会等待隐式编译。准备命令和语言验证见 [Espressif SDK 资源](ESPRESSIF_RUNTIME.md)，用户入口与实板边界见 [Espressif 支持](ESPRESSIF_SUPPORT.md)；许可证和厂商来源见 [Espressif 组件说明](../licenses/Espressif-NOTICE.md)。

- F7 自动保存打开的修改文件，验证工具集及工程版本锁，然后以参数数组启动内置 CMake/Ninja，不经过 shell。
- 每个 IDE 进程首次使用某工具集时完整计算 SHA-256；随后每次构建仍读取清单指纹，并枚举全部文件/目录，对比路径、长度、创建/修改时间和属性。与本次启动中成功校验的快照一致时复用结果，避免反复读取整套 GCC、库和脚本。工具集变化、校验取消/失败、重启 IDE 后均需重新完整校验；校验期间目录变化也拒绝缓存结果。
- 校验快照只存于内存，不写进工具链或工程。元数据复用用于加快日常构建，不等同于每次重新读取全部内容；主动保留长度和时间的内容更改应使用手动完整检查。清单指纹和工程工具版本锁仍每次核对，缺失/多余文件或链接会拒绝使用。
- 工具由相对安装路径解析。子进程 PATH 只含当前工具集和 Windows 系统目录，清除宿主 GCC/CMake/Python 环境变量的干扰。
- 生成 `.build/firmware.elf`、`.bin`、`.hex`、`.map`、`compile_commands.json` 和原始 `studiox-build.log`，用内置 size 显示大小。
- IDE 或工程位置改变时用 CMake `--fresh` 重建机器缓存。用户根 CMake 文件不重新生成。
- 同一服务只允许一个构建；停止/关闭窗口会取消并清理构建进程树。每个 CMake 阶段超时为 5 分钟，日志捕获有容量上限，截断/超时记录在日志中。
- 「工具 → 检查内置工具链」检查全量文件哈希及 GCC/G++/GDB/CMake/Ninja/OpenOCD 的 `--version`，不访问硬件。
- 手动检查始终绕过快照缓存；构建后立即下载可以复用同一服务已验证且未变化的工具集。
- 工具解析与构建/配置从后台入口执行；进度通过调用方传入的 `IProgress` 返回。目录枚举与缓存命中的同步解析也不会在 WPF 调度线程运行。完整校验以至少 250 毫秒间隔报告已检查文件数，取消仍使未完成的校验无法复用。

OpenOCD 脚本通过 `resourceDirectories.openocdScripts` 定位，带下载配置的器件包与已知 STM32F1/F4 CubeMX 工程共用顶部烧录器选择，执行编译、检查目标、写入、校验、复位流程。下载检查当前成功构建凭据和产物哈希，详见 [下载说明](DOWNLOAD.md)。GDB 调试会话待接入。CubeMX 代码索引使用实际编译数据库；器件包工程按模板定义解析。

## 验证范围

AG32 最小工程进行真实编译和产物检查；通用 Arm Cortex-M4 与 RISC-V RV32IMAC 探针检查 C/C++ 头文件、编译和库链接。通用探针采用 nosys 桩，未提供硬件系统调用，不能作为可上板固件。

用户随后明确授权烧录已编译的 AG32 最小固件：通过 CMSIS-DAP v2 完成 Flash 备份、1,304 字节固件写入、全量回读及复位后的心跳验证。逻辑区和选项字节未改变。此次测试使用内置 OpenOCD 加本机厂商 `agrv2k.cfg`，不等同于桌面下载/调试功能已经完成。结果见 验证记录（本地记录）。
