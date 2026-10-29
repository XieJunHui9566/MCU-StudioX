# PIC16 软件支持

Windows 0.2.6.15 支持 122 个精确 PIC16 型号的新建工程、C 编辑与寄存器提示、独立 XC8 编译、HEX/ELF 生成及一致性预检。PIC16F877A 增加 PICkit3 下载入口，其他 PIC 型号仍仅编译和导出。源码断点、单步、PIC18、PIC24 和 dsPIC 尚未接入。软件测试和实板结果分别记录。

## 是否需要 MPLAB X IDE

编译不需要安装或启动 MPLAB X IDE。Microchip 提供独立 XC8 Windows 压缩包和 DFP，StudioX 直接运行其命令行编译器；不修改系统 PATH，不安装全局 Java、Node 或 Python。

PICkit3 下载使用 StudioX 管理的私有 `microchip.ipe/6.20.0` 运行组件，内含 IPECMD、所需库、PIC16Fxxx 编程数据和私有 JRE。日常使用不安装或启动 MPLAB X/IPE，不依赖注册表中的 MPLAB 路径、系统 Java 或全局 PATH。6.20 是官方最后支持 PICkit3 的版本；不能使用本机较新的 6.35 替代。

`tools/Prepare-PicIpeRuntime.ps1` 是开发侧组件提取入口，核对官方安装器 SHA-256 `28DB195FB4277853A42CF34DA7A6F68A6453B77ED8530C9D9499E4CC67D17782` 和有效 Microchip 签名，保留原始许可并生成完整文件索引。一次提取后可移除提取用的外部安装。此脚本不作为终端用户操作步骤；本机私有组件准备不等于取得公开再分发授权，也不自动制作新安装包。

PICkit3 使用 Windows 通用 HID USB 驱动，无需安装 WinUSB、关闭签名校验或改绑其他 Microchip 工具驱动。连接后需核对具体设备绑定状态。

- [XC8 官方下载](https://www.microchip.com/en-us/tools-resources/develop/mplab-xc-compilers/xc8)
- [DFP 官方仓库](https://packs.download.microchip.com/)
- [IPE 安装说明](https://developerhelp.microchip.com/xwiki/bin/view/software-tools/ipe/installation/)
- [IPE 与 IPECMD](https://www.microchip.com/en-us/tools-resources/production/mplab-integrated-programming-environment)
- [Microchip 对第三代工具支持的说明](https://www.microchip.com/en-us/about/media-center/blog/2024/discontinued-ide-support-for-gen3-tools)
- [PICkit3 安装海报](https://ww1.microchip.com/downloads/aemDocuments/documents/OTH/ProductDocuments/MISC/50002010B.pdf)

## 已核实的器件与工具

PIC16F877A：8192 个 14 位程序字、368 B 分银行 RAM、256 B EEPROM，依据 Microchip PIC16F87XA 数据手册 DS39582B 及 DFP 1.9.176 中的精确型号。`flashBytes=14336` 仅表示物理位容量折算；`ramOrigin=0` 为容量描述占位，不能解释成连续 RAM 区间。PIC 元数据和 XC8 输出保留程序字、RAM、EEPROM 单位；不交给通用 GCC 内存统计或 OpenOCD。

完整型号表在 `src/StudioX.Packages/Pic16Catalog.json`。由 `tools/Update-Pic16Catalog.py` 从官方 DFP 的 PDSC/EDC 提取：程序空间统计 CodeSector，RAM 统计排除 shadowidref 镜像的 GPRDataSector，EEPROM 统计 EEDataSector。包括 9 个 12 位指令字型号和 113 个 14 位指令字型号；F/LF/HV 身份分别保留。七个包按 PIC16F5xx、6xx、7xx、8xx、9xx、HV 和原 PIC16F877A 单器件包交付；不能通过改名增加未核实型号。

HV 包修订 0.1.1 单独标明 PIC16HV540 的 EPROM 程序存储，依据该型号 EDC 的 `memtech=eprom`；它不能被表述为可反复擦写的 Flash。通用 `flashBytes` 字段在 PIC 中仅承载程序存储物理位容量折算，烧录能力不能由该字段推断。

固定组件为 `microchip.xc8/4.0.0`，编译器身份 `xc8-4.00-pic`：

| 资产 | 版本 | SHA-256 |
| --- | --- | --- |
| XC8 Windows 压缩包 | 4.00 | `1AB191FD446E1A53EBB039E95AD695C6F18BEF0B2EE3DDD9F08268A46DDDB9F5` |
| PIC16Fxxx DFP | 1.9.176 | `C5C3CE16F908635A6858FC128494B82AE9AA61B7F5AB932A12843BBDFA8F8F62` |

来源：
- https://ww1.microchip.com/downloads/aemDocuments/documents/DEV/ProductDocuments/SoftwareTools/xc8-v4.00-full-install-windows-x64.tar.xz
- https://packs.download.microchip.com/Microchip.PIC16Fxxx_DFP.1.9.176.atpack
- https://ww1.microchip.com/downloads/en/devicedoc/39582b.pdf

`tools/Prepare-PicRuntime.ps1` 接受用户取得的这两个官方归档，核对固定摘要及编译器的有效 Microchip 数字签名，在指定私有 runtime 中准备组件，保留完整原始许可证和来源索引。安装目录同时提供 `准备PIC工具.ps1`：在 PowerShell 7 中传入 `-CompilerArchive`、`-DfpArchive`、`-RuntimeDirectory <IDE安装目录/runtime>` 和已有 `-SevenZipPath`。用户需自行从官方取得并接受 XC8 许可；未准备组件时 IDE 会明确显示缺失工具。公开轻量版和完整版都不附带专有 XC8 编译器，PIC 器件包本身不包含编译器。免费优化功能不代表已取得再分发授权。

## 使用和边界

用 `tools/New-PicPack.ps1` 和构建的 CLI 制作七个 StudioX 格式 1 器件包。导入后，在通用新建页选择 Microchip、准确型号和对应 XC8 模板。

- 基础 C：软件计数器与 CLRWDT，不假设晶振或外部引脚，配置位待设置。
- GPIO 示例仅供 PIC16F877A：明确为外部 4 MHz / XT / RD0。RD0 不代表未知板卡的 LED；实际接线和时钟必须另行核实。

第一阶段使用 C99 / O0，编译 `src/` 下 1～128 个 C 文件、`include/` 和 `src/` 头文件。不使用 GCC 参数、CMake、Arduino 或独立汇编输入。板级配置位保留在用户 C 源文件中，不能自动套用尚未确认的硬件。

编译前通过共同组件管理流程核验精确版本与完整文件摘要并锁定内容。源码进入独立用户数据目录的私有快照，XC8 中间产物不写入源码目录；构建记录和原始日志复制到 `.build/pic/<运行编号>/`。失败或输入变化撤销 `.studiox/pic-build.json`；HEX 导出预检重新核对源码、工具和 HEX/ELF 摘要，不包含任何烧录授权。

clangd 使用通用 C 解析包装入口，引用已校验 DFP 的真实寄存器声明；包装入口仅供分析器使用，不参与实际编译。可提供寄存器/位字段补全与只读跳转；PIC 位类型、绝对地址、整数宽度、调用约定及 GCC 不支持的扩展，以真实 XC8 编译为准。

## PIC16F877A / PICkit3 下载

打开 PIC16F877A 工程，在下载设置刷新并明确选择 PICkit3 序列号，目标板独立供电。连接 MCLR/VPP、VDD 检测、GND、PGD/RB7 和 PGC/RB6；按板卡原理图核对方向及电压。此入口不向目标板供电。

下载操作保存并编译源码，展示固件 SHA-256、程序字数、配置字及所选序列号。确认绑定本次构建凭据、组件指纹及工程设置。随后重新检查当前源码和 HEX/ELF，按相同序列号核对实际器件，先回读到 `.build/pic-download/<运行编号>/before.hex`，再写入并校验。新构建、设置变化、备份失败、器件不匹配或代码保护均阻止旧确认继续写入。

首阶段固件必须包含程序复位入口及明确配置字，只允许程序和配置区域，不允许隐式修改 EEPROM、用户 ID 或设备 ID。下载使用 `-Z` 与 `-OID` 保留 EEPROM/用户 ID，不调用独立整片擦除、解除保护、OTP 或恢复命令。官方后端首次选择器件时可能为 PICkit3 加载适用工作固件，此时不要断开烧录器。目标原程序回读不代表已授权覆盖。

原始标准输出、错误和异常保存在每次下载的 `download.log`；成功要求工具退出成功且明确报告校验成功，退出码 0 本身不足以报告下载成功。Java 的区域、编码和工作目录由服务控制，日志及用户状态写入独立用户数据目录，不修改已校验组件。纯枚举 `-T` 的官方 6.20 退出码 50 仅在输出是完整列表且无错误时接受；写入和回读不适用该例外。全局探针租约、设备所有权和工具使用租约阻止会话冲突。

2026-10-30 实板记录：Windows HID 驱动识别 PICkit3 `BUR195068601`（VID 04D8 / PID 900A），官方后端确认 PIC16F877A、revision 8。StudioX 应用服务成功备份、写入 RAM 计数固件并校验，随后恢复原固件并校验。之后使用同一服务下载 PIC-DIP40 V2 / 用户确认的 4 MHz / LCD1602 工程，用户确认屏幕显示 `Hello, World!`；结束后保留该程序运行。当前证据只覆盖这块 PIC16F877A，不代表其他型号或源码调试已验收。

LCD 工程位于本机 `E:/MCU/Projects/PIC16F877A_LCD1602_HelloWorld`，包含已核对原理图。数据口为 RD0–RD7，RS/RW/E 为 RE0/RE1/RE2；LCD 使用前断电移除 LED-EN（LED-VCC/J3）跳帽。固件为 215 个程序字、9 字节 RAM，保持原配置字 `0x3F32`。详细证据位于 `.artifacts/pic16f877a-lcd1602-20261030`，下载集成及回滚证据位于 `.artifacts/pickit3-download-20261029`。这些路径仅为本机验收记录，不参与生产参数生成。

## IDE 本体源码改动

涉及本体：Packages 提供 `pic12` / `pic14` 与精确器件元数据；Engine 负责 XC8 构建凭据、HEX 校验和 IPECMD；Application 管理设备所有权、健康与组件需求；Desktop 调用应用服务提供 PICkit3 设置与确认，隐藏其他 PIC 型号的下载及所有 PIC 调试入口。插件 SDK 当前不提供完整构建后端注册点。

## 验证入口

`tools/StudioX.PicValidation` 验证精确型号与容量、两个模板真实编译、中文/空格路径、多 C 文件、错误原始诊断、源码/HEX 变化拦截、并发编辑拒绝、内容锁、关闭 OpenOCD 入口及真实 clangd。

`StudioX.PicValidation --catalog-matrix <新证据目录> <runtime> <七个包所在目录>` 通过真实应用服务逐型号创建、检查、实际 XC8 编译并校验 HEX/ELF 凭据，必须覆盖全部 122 个型号才通过。

`StudioX.PicValidation --download <新证据目录> <runtime> <PIC16F877A包>` 使用真实 XC8 产物及隔离的工具进程夹具覆盖确认失效、错误目标、保护、备份失败、无校验记录、失败及取消；最终只枚举实际 PICkit3，不写硬件。实板回读和下载必须另留证据。

`MCU StudioX.exe --preview-pic <新证据目录> <runtime> <器件包> <已创建PIC工程>` 验证真实 WPF 通用模板、工程编辑、健康检查、编译、HEX 产物和深浅主题。该入口不运行 PICkit 或硬件会话。
