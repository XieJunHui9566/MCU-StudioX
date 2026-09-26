# 下载与烧录

CH32V203 新增独立包和 WCH-Link / SDI 下载配置，按具体型号核对 Flash/RAM，不套用 V307 内存划分。C8T6 + WCH-LinkE 已完成最小固件下载、校验和物理 Flash 回读，其他型号仍为软件验证，见 [V203 实板记录](CH32V203-DEBUG-ACCEPTANCE-20260923.md)。IDE 遇到读保护仍拒绝自动解锁。

工程共用顶部「烧录器」下拉栏。STM32F1/F4 提供 ST-Link、DAP-Link (CMSIS-DAP)、J-Link；器件包工程采用包声明的烧录器、传输协议和默认速度，CubeMX 导入工程采用 IDE 中匹配具体型号的下载目录。HAL、标准库及带 FreeRTOS 的模板使用相同入口。

选择保存在当前工程 `.studiox/download.json`。旁边齿轮和「构建 → 下载设置」可以设置速度（通常为 100–15000 kHz）及可选烧录器序列号；WCH SDI 限定为 400、4000、6000 kHz，仅支持单台连接、序列号留空。更换烧录器会清除旧序列号并恢复新烧录器的默认速度。设置对话框只保存设置，不连接硬件。关闭工程清空下拉栏，忙碌期间禁用选择和再次下载。

点击「下载」依次保存已打开的源文件、编译、检查构建结果、连接目标、核对目标身份、按映像范围擦写、校验、复位运行。底部显示原始 OpenOCD 输出以及中文成功/失败和退出码。停止操作会终止工具进程树；中途停止不能视为烧录成功，应重新下载。日志保存在 `.build/download-<会话>/openocd.log`，取消也保留已收到的输出。

AI/MCP 的 OpenOCD 入口使用 `firmware_download_plan` 只读预检已成功编译的唯一固件，再使用 `firmware_download` 指定预检返回的芯片型号、完整 SHA-256、探针与速度。宿主会对本次真实烧录单独请求授权，审批后仍重新核对固件与芯片；不自动编译、不接受任意文件路径。内置 Agent 和外部 stdio 客户端使用同一套工具。

STC 串口 ISP 在 MCP 中使用独立的 `stc_isp_plan` 与 `stc_isp_download`；预检不创建快照或打开 COM 口。下载工具要求复述 HEX 哈希、准确型号、COM 口、波特率与时钟选项，并明确承认原程序会按芯片协议被擦除（可能整片擦除）、没有原程序备份和 Flash 读回校验。授权后才创建单份固件快照并再次核对，详情见 [STC ISP](STC-ISP.md)。

## 构建产物

- 每次成功构建产生 `.build/studiox-build-receipt.json`，记录工程配置、工具集指纹、真实固件路径与 SHA-256。开始构建/配置时作废旧凭据，失败和取消不能继续使用旧固件。
- 器件包工程沿用其 `.build/firmware.bin` 和清单 Flash 地址。CubeMX 使用 CMake File API 实际报告的 ELF 目标，不要求命名为 firmware，也不采用可能经过用户签名/填充的 BIN 文件。
- ELF 必须是与目标匹配的 ARM / RISC-V 32 位小端可执行映像，所有非空 PT_LOAD 段的物理装载地址都要落在已知芯片的应用 Flash 内；不允许段重叠。RAM `.data` 使用 Flash 装载地址，BSS 不下载。保留原链接脚本地址，传给 OpenOCD 的额外偏移为 0。
- 下载前核对工具锁、构建凭据和产物哈希，再创建独立映像快照。目标脚本在任何擦写之前校验硅片组和标称 Flash 容量；不自动解锁、不写选项字节、不执行整片擦除。
- 存在多个可执行目标时明确报错，不擅自选择其中一个。目前要求工程保留一个可下载的应用目标；外部 Flash、RAM 下载及签名镜像工作流尚未适配。

OpenOCD 擦写/校验语义参考：[Flash Commands](https://openocd.org/doc/html/Flash-Commands.html)、[Image loading commands](https://openocd.org/doc/html/General-Commands.html#Image-loading-commands)。

## 支持范围与维护

CubeMX 下载目录包含已有 F1/F4 包中的 244 个基础型号，容量和目标保护脚本从固定版本的 StudioX 器件包生成，不按型号字符串猜测容量。`tools/New-Stm32DownloadCatalog.ps1` 生成 Engine 嵌入资源 `Resources/stm32-download.json`，其中记录来源包 SHA-256；包的 SDK/Keil PDSC 来源见 `examples/packs/st.stm32-series/*-provenance.json`。可识别例如 STM32F407ZG / STM32F407ZGT6 的明确订货号，含 `(E-G)` 的不确定容量名称不启用下载。CubeMX 改换型号后需重新导入元数据。

框架向所有工程类型开放，但并非任意芯片都支持三种烧录器。没有 `openOcd` 定义的器件包保持下载不可用，包括旧 `studiox.preview.ag32vf303 0.1.0` 包。AG32 0.1.1 新包增加官方 AGM BLASTER 的独立入口、专用 OpenOCD 和 156 KiB 应用区限制，要求已核实的 100 KiB 未压缩逻辑布局；不将 STM32 的配置套到 RISC-V AG32。详见 [AG32 适配记录](AG32-DEBUG-ACCEPTANCE-20260922.md)。

CH32V307 的 `wch.ch32v307/0.1.1` 包提供 WCH-Link / WCH-LinkE（RISC-V / SDI）入口，使用独立的沁恒工具集。接口脚本随器件包提供，显式打开厂商 `page_erase` 模式，避免其默认全代码区擦除。写入前核对具体型号、读保护状态和 256 KiB Flash / 64 KiB RAM 划分；不自动改变选项字节。下载后执行 `reset halt; resume`：当前沁恒 OpenOCD 的 `reset run` 在实测中会停留在复位入口。下载配置不代表已支持交互式调试；旧 0.1.0 工程不会自动更换器件配置。实机范围与证据见 [CH32V307 验证记录](CH32V307_VERIFICATION.md)。

上述 STM32 下载目录的初始验证为离线检查；后续 ST-Link / DAP 实机记录见对应调试验收文档。设备在 Windows 上需要其对应 USB 驱动，J-Link 型号/固件也须支持目标使用的 SWD/JTAG 协议。

## 可重复验证

`dotnet run --project tools/StudioX.DownloadValidation -- <runtime> <new-output> <isolated-CubeMX-F103> <isolated-CubeMX-F407> <artifacts/packs>`

该工具复制隔离副本，真实编译 F103/F407 CubeMX 及原生 SPL、HAL+FreeRTOS 工程，通过三种接口解析 OpenOCD 配置；用模拟读数检查型号/容量拒绝分支，不执行 `init`。另检查选项保存、产物替换、坏 ELF、地址越界、CPU 错误、地址偏移、多目标拒绝、不明确型号、失败/取消后拒绝旧固件，以及 AG32 官方烧录器和应用区限制。

`MCU StudioX.exe --preview-download <new-output> <validation-output>`

桌面检查选择持久化、工程切换、速度/序列号恢复、关闭/忙碌状态，以及深浅主题和最小宽度工具栏。不会点击下载按钮或访问硬件。

WCH 离线检查：`dotnet run --project tools/StudioX.WchValidation -c Release -- --download <runtime> <wch.ch32v307-0.1.1.mcupack> <new-output>`。实际编译三个封装模板，并以模拟寄存器执行包内 Tcl 型号、内存划分、读保护拒绝分支；此命令不执行 `init`、不访问硬件。
