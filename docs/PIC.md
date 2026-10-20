# PIC16 软件支持

Windows 0.2.6.15 支持 122 个精确 PIC16 型号的新建工程、C 编辑与寄存器提示、独立 XC8 编译、HEX/ELF 生成及一致性预检。当前没有 PICkit/IPECMD 烧录、源码断点、单步或实板验收。PIC18、PIC24 和 dsPIC 尚未接入。

## 是否需要 MPLAB X IDE

编译不需要安装或启动 MPLAB X IDE。Microchip 提供独立 XC8 Windows 压缩包和 DFP，StudioX 直接运行其命令行编译器；不修改系统 PATH，不安装全局 Java、Node 或 Python。

烧录是后续独立阶段。官方 IPE/IPECMD 目前由 MPLAB X 安装器分发，安装时可选择所需组件；不能据此承诺不需要任何 Microchip 运行组件。日常操作可以由 StudioX 调用后台工具，不必同时打开 MPLAB 的编辑界面。烧录器、DFP 和工具版本的组合仍须核验，并在获得实板授权后验收。

- [XC8 官方下载](https://www.microchip.com/en-us/tools-resources/develop/mplab-xc-compilers/xc8)
- [DFP 官方仓库](https://packs.download.microchip.com/)
- [IPE 安装说明](https://developerhelp.microchip.com/xwiki/bin/view/software-tools/ipe/installation/)
- [IPE 与 IPECMD](https://www.microchip.com/en-us/tools-resources/production/mplab-integrated-programming-environment)

## 已核实的器件与工具

PIC16F877A：8192 个 14 位程序字、368 B 分银行 RAM、256 B EEPROM，依据 Microchip PIC16F87XA 数据手册 DS39582B 及 DFP 1.9.176 中的精确型号。`flashBytes=14336` 仅表示物理位容量折算；`ramOrigin=0` 为容量描述占位，不能解释成连续 RAM 区间。PIC 元数据和 XC8 输出保留程序字、RAM、EEPROM 单位；不交给通用 GCC 内存统计或 OpenOCD。

完整型号表在 `src/StudioX.Packages/Pic16Catalog.json`。由 `tools/Update-Pic16Catalog.py` 从官方 DFP 的 PDSC/EDC 提取：程序空间统计 CodeSector，RAM 统计排除 shadowidref 镜像的 GPRDataSector，EEPROM 统计 EEDataSector。包括 9 个 12 位指令字型号和 113 个 14 位指令字型号；F/LF/HV 身份分别保留。七个包按 PIC16F5xx、6xx、7xx、8xx、9xx、HV 和原 PIC16F877A 单器件包交付；不能通过改名增加未核实型号。

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

## IDE 本体源码改动

涉及本体：Packages 增加 `pic12` / `pic14` 和受严格约束的 PIC 型号/程序字元数据；Engine 增加 PIC 工程类型、独立构建/HEX 凭据、工具用途及工程创建/校验分支；Application 接入健康检查、器件详情与语言分析；Desktop 接入通用模板与编译呈现，隐藏不支持的 GCC 设置、烧录和调试入口。插件 SDK 当前不提供完整构建后端注册点，不能仅靠用户插件完成。

## 验证入口

`tools/StudioX.PicValidation` 验证精确型号与容量、两个模板真实编译、中文/空格路径、多 C 文件、错误原始诊断、源码/HEX 变化拦截、并发编辑拒绝、内容锁、关闭 OpenOCD 入口及真实 clangd。

`StudioX.PicValidation --catalog-matrix <新证据目录> <runtime> <七个包所在目录>` 通过真实应用服务逐型号创建、检查、实际 XC8 编译并校验 HEX/ELF 凭据，必须覆盖全部 122 个型号才通过。

`MCU StudioX.exe --preview-pic <新证据目录> <runtime> <器件包> <已创建PIC工程>` 验证真实 WPF 通用模板、工程编辑、健康检查、编译、HEX 产物和深浅主题。该入口不运行 PICkit 或硬件会话。
