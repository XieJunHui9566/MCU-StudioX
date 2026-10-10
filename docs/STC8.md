# STC 8 位软件适配

## 范围

`stc.stc8/0.1.2` 在 0.1.0 的 SDK、模板和编译工具版本基础上增加 IAP15F2K61S2 的包内 Mon51 V2.5 制作固件；IDE 直接读取和下载，无需用户提供厂商程序。制作只接受 ISP 7.2.5S / 状态 70。旧工程编译锁不迁移，可在调试窗口独立选择此监控资源包。流程和边界见 [Mon51 界面](STC-MON51-UI.md) 与 [固件说明](STC-MON51-FIRMWARE.md)。

`stc.stc8/0.1.0` 是单个 StudioX Pack，首批收录 STC89、STC12、STC15、STC8G、STC8H 共 24 个已核实型号，其中 IAP15F2K61S2 为代表型号。完整型号、容量与来源见 [包配方](../examples/packs/stc.stc8/README.md) 和 `artifacts/packs/STC8-0.1.0/source/vendor/provenance.json`。型号未列入包时，新建工程不猜测兼容型号或内存容量。

直接运行随附发行目录时，首次进入“新建工程”会按 `device-packs/index.json` 校验并导入用户目录中缺失的器件包；已有同 ID 和版本的用户包保持不变。`STC / 宏晶科技` 在厂商选择器中显示为“宏晶科技”。

工程仅生成 C 源码，以 `stc.sdcc/1.0.0` 内的 SDCC 4.5.0、CMake 4.4.0、Ninja 1.10.2 编译。固件产物为 `.ihx`、相同内容的 `.hex`、`.map` 与 `.mem`；内存视图读取 SDCC `.mem` 的程序 Flash 和扩展 RAM 统计，不把它解释为 GCC/ELF。IDE 已接入由随附 `stcgal 1.10` 执行的串口 ISP 下载，也可选择用户自己安装的版本；仍不提供 STC 源码调试。实际串口、供电和复位方式须按开发板确认。

## 编译档位

| IDE 选项 | SDCC 参数 | 含义 |
| --- | --- | --- |
| 默认 | 无额外优化参数 | SDCC 默认策略 |
| 低优化 | `--no-peep --nogcse --noinvariant --noinduction --noloopreverse --nolabelopt --nolospre` | 关闭列出的优化过程；不等价于 GCC `-O0` |
| 代码尺寸 | `--opt-code-size` | 优先减小代码 |
| 执行速度 | `--opt-code-speed` | 优先提高速度 |

档位会核对 `compile_commands.json` 中的实际 SDCC 命令。STC 工程不提供 GCC 调试信息选项。SDCC 参数语义见 [官方手册](https://sdcc.sourceforge.net/doc/sdccman.pdf)。

STC15 系列按官方手册在物理程序容量末尾保留 7 字节 Global ID：IAP15F2K61S2 的物理程序空间为 61 KiB，SDCC `--code-size` 限为 61 KiB 减 7 字节。其他型号按各自官方容量配置；详情与资料链接在 [包配方](../examples/packs/stc.stc8/README.md)。

工程设置中的“程序 Flash 容量上限”默认沿用器件包 `--code-size`；可选值以字节保存至 `.studiox/build.json`，范围为 1024 字节至该型号的有效上限。保存、构建前按包内型号校验，构建后对照 SDCC `.mem` 的实际 Max 和 HEX 地址，避免配置越过型号容量或 STC15 的 7 字节保留区。这个选项只限制链接产物可占的 ROM，不会改变芯片物理 Flash，也不会替烧录工具选择目标型号。

明确选择内部 RC 或外部晶振并填写频率后，IDE 为 C 编译定义 `STUDIOX_CLOCK_HZ=<频率 Hz>UL`，工程可据此计算 UART 波特率或定时器重装值；未指定频率时不定义。时钟频率按整数 Hz 保存，可准确填写 11.0592 MHz。修改时钟模式或频率会使旧构建凭据失效；仅改变串口号或传输波特率不会。宏本身不会更改芯片时钟，选定的时钟源或 RC 校准值仅在确认下载后由 ISP 工具写入。已有代码需要主动使用该宏，才能随频率改变定时计算。

## 时钟与串口 ISP

可选时钟模式由**所选型号**决定，不会把某系列的能力推断到另一系列：

| 系列 | 时钟选项 | 频率行为 |
| --- | --- | --- |
| STC15 / IAP15 | 保留时钟来源、内部 RC、外部晶振 | 内部 RC 可设 5–28 MHz；外部晶振填写板上实装频率，ISP 会切换时钟源，也可能重写备用 RC 校准值 |
| STC12 | 保留时钟来源、内部 RC、外部晶振 | 此系列的 `stcgal` 不校准 RC；外部晶振频率用于工程编译，晶振须实装 |
| STC8G / STC8H | 保留时钟来源、内部 RC | 内部 RC 可设 4–36 MHz；当前 `stcgal 1.10` 不开放外部时钟源切换 |
| STC89 | 保留时钟来源 | 当前不开放 RC 校准或时钟源切换 |

“保留时钟来源”不会向 `stcgal` 发送切换内/外部时钟源的选项，但不表示 RC 校准值逐位不变。对 STC15/IAP15，`stcgal 1.10` 在每次下载时仍以芯片报告的当前频率为目标重新校准内部 RC，并在固件写入后写回新校准值；实际频率可能略变。若芯片当前使用外部晶振，时钟来源保持外部，但工具会把备用内部 RC 校准值写为出厂 24 MHz 参数。选择“内部 RC”而不填写频率也会按当前频率重新校准。

外部晶振选项不能生成晶振时钟；赛点 V3.1 的 IAP15F2K61S2 开发板没有 MCU 外部晶振，使用这块板时应保留当前时钟来源或选择内部 RC。频率、模式、COM 口和传输波特率保存在工程的 `.studiox/stc-isp.json`，而 `stcgal.exe` 的本机路径单独保存在用户数据目录，不写进工程。

### 在 IDE 中下载

1. 新建或打开准确型号的 STC 工程。在工程设置中选择优化档位，按需填写“程序 Flash 容量上限”（单位字节；留空沿用器件包上限），点击“保存参数”。
2. 在“STC 串口下载与时钟”中选择该型号支持的时钟模式、需要时填写 MHz 频率，选择开发板 COM 口和传输波特率，再点击“保存时钟与下载设置”。发行版已内置 `stcgal 1.10`；若工具状态显示不可用，可安装该版本及其 Python 依赖，使用“选择 stcgal 工具…”定位 `stcgal.exe`。关闭占用该 COM 口的串口监视器。
3. 点击“下载”。IDE 会先保存源码、编译固件，核对工程型号、器件包容量、构建凭据、HEX 地址与 SHA-256，然后显示目标型号、串口、固件路径、哈希和时钟选项。确认窗口默认选“否”；只有明确确认后才打开串口。**下载会擦除并覆盖板内现有程序，STC ISP 无法把旧程序读回备份。**
4. 工具日志开始显示等待单片机时，按开发板的下载/上电按钮完成上电进入 ISP。赛点 V3.1 板使用 S2：按住约 1 秒再松开。IDE 在擦写前核对芯片报告的准确型号与程序容量；不匹配时拒绝写入。完成后查看下载日志，并通过板上行为或串口输出确认程序运行。

IDE 只在 `stcgal` 报告完整写入时显示下载完成；该开发环境组件不做 Flash 读回校验。下载超时、中断或失败后若已开始擦写，板内程序可能不完整。STC 调试入口尚未接入。

## 来源和重建

包内五份 SDCC 寄存器头来自本机 AiCube 静态解包中的地址事实。构建脚本逐份核对源 SHA-256，规范化生成 SFR/sbit/xdata 声明；原始文件的再分发许可未找到，原文未打入包。来源、原始资源和生成物哈希记录于包内 `vendor/provenance.json`。发行版包含 SDCC 的原始许可证和工具文件索引；开发环境组件重建见 [工具说明](TOOLCHAINS.md)。

```powershell
& .\tools\Prepare-SdccToolRuntime.ps1 -SdccDirectory '<本机 SDCC 安装目录>'
python .\tools\New-Stc8Pack.py --aicube-root '<本机 AiCube 根目录>' --output 'artifacts/packs/STC8-0.1.0'
dotnet run --project tools/StudioX.StcValidation -c Release -- artifacts/tool-runtime artifacts/packs/STC8-0.1.0/stc.stc8-0.1.0.mcupack artifacts/validation/<新的输出目录>
```

现有离线验收在 `artifacts/validation/STC8-20260923-R3/matrix.json`：24 个型号创建工程并实编，IAP15F2K61S2 另验四档优化，合计 27 次构建；所有 HEX 记录校验和有效，代码地址未越过各型号可用 Flash。测试没有连接或访问硬件，不能据此声明开发板已通过。

新增程序容量与时钟宏后，`artifacts/validation/STC8-ROM-CLOCK-MATRIX-20260924-R1/matrix.json` 对 24 个型号完成 29 次实编；IAP15F2K61S2 的 8 KiB 可选上限和精确 11,059,200 Hz 宏也通过验证。`artifacts/validation/STC8-ROM-CLOCK-20260924-R5/matrix.json` 进一步确认仅修改 COM 口/传输波特率不使旧固件失效。新 IDE 下载入口尚未在实板执行；这些步骤均为离线构建或代码门禁验证。

## IAP15F2K61S2 实板验收

2026-09-23，在用户确认可覆盖原固件后，将 StudioX BuildService 编译的 LCD1602 与 UART 测试镜像（本地记录）经 CH340 COM5 和命令行 `stcgal 1.10` 下载到赛点开发板。ISP 确认目标为 IAP15F2K61S2，擦除、写入和配置均完成。随后以 9600 8N1 连续读取到 5 条预期 UART 标识，用户确认 LCD1602 两行文字正常显示。具体镜像哈希、芯片 UID 和结果见 实板记录（本地记录）。该结果适用于这块板和测试固件，不代表其余 23 个型号或新 IDE 下载入口的实板验收。

## STC32G/C251

STC 官方[软件下载页](https://www.stcmicro.com/cn/rjxz.html)将 **Keil C251** 列为 STC32G 的编译软件；[Keil 产品页](https://www.keil.com/products/c251/c251.asp)说明它面向 MCS-251。[5.60 评估版下载](https://www.keil.com/c251/demo/eval/c251.htm)需填写联系信息，且[官方评估限制](https://www.keil.com/demo/limits.asp)把 C251 目标代码限制为 2 KiB。本机未发现可用的 C251 安装，因此没有可重现的本地编译器可供集成；当前 SDCC `mcs51` 开发环境组件不用于 STC32G，也不把 STC32G 混入此 8 位包。后续若取得可用 C251 环境，需单独验证工程、链接与固件格式。
