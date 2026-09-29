# ARM32 通用器件包扩展

本轮交付目录：`artifacts/packs/ARM32-2026-09-30/`。该目录的 `README.md`、`catalog.json`、`devices.csv` 和 `summary.json` 是实际交付清单；候选目录不用于安装或发布。

产品仍面向多厂商 MCU。新增器件通过 StudioX 格式 1 数据包接入，没有把品牌分支写进 IDE Desktop，也没有新增全局运行环境依赖。此轮不修改 IDE 版本、不制作安装包、不安装用户器件包。

公开下载以 [MCUPacks 仓库](https://github.com/XieJunHui9566/MCU-StudioX-MCUPacks) 的索引和 [2026-09-30 发布说明](https://github.com/XieJunHui9566/MCU-StudioX-MCUPacks/blob/main/UPDATE-ARM32-2026-09-30.md) 为准。公开目录新增 **272 包 / 2,777 个器件条目**，连同已有公开版本，共 **325 个 ARM32 包 / 3,453 个器件条目**。整个仓库含其他架构在内共 334 包。

本地验证集合为 330 包 / 3,499 条目，不能整包原样上传：公开目录继续使用已有的 STM32 HAL-only 和其他较新修订，保留 GD32E51x 的既有不公开决定。新增 N32G003、N32G430 的 SDK 明确限制未经许可的分发；M0A21、NDA102 所选文件的授权条款尚不完整，这 4 包未公开。逐项原因见 `examples/packs/arm32-expansion/publication-policy.json`。

## 覆盖与能力

- 补齐常见 STM32F0/F2/F3/F7/G0/G4/L0/L1/L4/H7/C0/U0 中有匹配官方资料的单核型号；本地 F1/F4 包保留其已有模板，公开包继续保留 HAL / HAL + FreeRTOS 两种模板，不重新分发本地 SPL 内容。
- 新增 NXP LPC8xx/LPC11xx/LPC17xx、Kinetis KL26/K64，Nordic nRF51/nRF52，Microchip SAMD21/SAMD51。
- 新增雅特力 AT32、华大 HC32、灵动 MM32、极海 APM32、国民技术 N32、新唐 NuMicro 的多个常见子系列。
- 整理现有 GD32、PY32 和 RP2040/RP2350 包，按包 ID 去重收录，保留原有功能和模板。

新增包提供 **C/CMSIS 基础工程与离线构建支持**，不是所有厂商 HAL/SPL、RTOS、USB/BLE 协议栈的完整移植。包内的驱动头文件属于 SDK 内容；只有清单中列出的系统及依赖源码参与构建，不能把有函数声明误当成完整外设库已链接。图形引脚分配、下载和调试需要后续逐系列建立准确的器件数据库与目标身份检查。

本轮未连接硬件。新增包不声明 OpenOCD 能力，现有包的能力不代表本轮重复完成实板验收。

## 数据和启动约束

型号、内存、内核、预处理宏取自原厂 PDSC 的继承关系；不依据型号字符串猜容量。连续主 Flash bank 可合并，不连续 bank、多核/安全域依赖以及资料缺失的器件明确排除。链接器使用声明的主 RAM 区，完整内存证据随包保留；不把 CCM、DTCM、备份 RAM 盲目拼成一个连续区。

STM32 和 Microchip 使用原厂 GCC 启动文件。ARMASM 启动文件转换时，保留向量顺序及厂商复位步骤（POR、RAM 供电、勘误、VTOR 等），只将 ARM 运行库入口替换为本包的数据段/BSS/构造函数初始化入口。未知启动语法停止生成。原始启动文件、SHA-256 和转换结果一起交付。

LPC 保留向量校验和。Kinetis 在 `0x400` 生成原厂默认的 Flash 配置字，构建验收检查其未启用加密；不执行配置写入。AT32F403A/F407 的 RAM 使用 DFP 默认 96 KiB 配置，没有直接套用可选扩容容量。

模板的 `main.c` 只调用 `StudioX_System_Init()` 并进入等待中断循环。`StudioX_System.h/.c` 集中提供毫秒时基；默认时钟来自厂商系统文件，需与实际板卡核对。旧 MM32 SDK 等没有动态 `SystemCoreClockUpdate()` 时，使用厂商初始化配套变量，并明确标记 `STUDIOX_CLOCK_FROM_SYSTEM_INIT`。改变时钟后应同步修正变量和 SysTick。涉及晶振、RTOS、低功耗停钟或自定义 SysTick 的工程需维护自己的时基。

已记录的适配包括 STM32G483 宏大小写、APM32F0 家族选择宏、N32/AT32 系统时钟 API 命名、HC32F072 按封装头文件转接与 `uint32_t` 声明修正。HC32F072 的原始系统源文件另存 `vendor/original/`，不会丢失原始厂商证据。

## 可复现流程

维护者可从 `examples/packs/arm32-expansion/sources.lock.json` 获取已审查的版本、提交和哈希。`Get-Arm32ExpansionSources.py` / `Get-Arm32StSources.py` 仅用于发现候选版本；复现交付来源使用以下锁定下载器。它只提取官方包的数据，不执行安装程序：

```powershell
python tools/Get-LockedArm32Sources.py --output artifacts/vendor/arm32-locked
python tools/New-Arm32ExpansionPacks.py `
  --sources artifacts/vendor/arm32-locked `
  --cmsis <ARM.CMSIS.6.3.0解压目录> `
  --output artifacts/packs/arm32-new-candidate
```

这里的 Python 是维护者运行生成器的工具，最终用户导入和构建 `.mcupack` 不需要安装 Python。生成器核对 Core 头文件哈希，使用现有 IDE 管理的 `arm.gnu/1.0.0` / `arm-gnu-15.2.rel1`，不会修改系统 PATH。

离线验证器链接现有 IDE 的应用/引擎/包服务，测试真实导入、工程生成、CMake/Ninja/GCC 构建及 ELF/BIN/HEX，而不是另做一套模拟构建逻辑：

```powershell
dotnet build tools/StudioX.Arm32ExpansionValidation -c Release `
  -p:StudioXDirectory="<现有IDE安装目录>" `
  --artifacts-path artifacts/build/arm32-expansion-validation
pwsh -File tools/Test-Arm32Expansion.ps1 `
  -Packages <候选packages目录> -Runtime <IDE的runtime目录> `
  -Validator <验证器DLL路径> -Output <全新验收目录>
python tools/test_arm32_pack_recipes.py
```

所有型号都检查定义并实际生成工程；同一 CPU/宏/启动/模板组合选择 Flash/RAM 容量边界进行真实编译。包标注的实编次数与工程生成次数分别统计。失败日志原样保留。`Complete-Arm32PackSet.py` 将验证仓库的内容哈希与待交付归档对照，要求完整型号矩阵通过后才创建交付目录；失败、缺测和内容不匹配都会阻止交付。

每个包保留来源和许可证，官方 SDK 的许可不会被替换成本项目许可。暂不支持的条目及原因见交付目录 `validation/excluded.json`，不通过跨系列套头文件来扩大支持数量。

## 公开发布

`Prepare-Arm32PublicRelease.py` 从完整通过的本地交付目录和已有公开目录创建全新发布目录；不会替换已有包。政策文件锁定官方补充许可证的提交、URL 和 SHA-256，并列出不公开的包。极海补齐相应系列官方软件许可；6 个 NXP MCUXpresso 包补齐 BSD-3-Clause 正文。所有转换过的启动文件添加日期和变更说明，保留原始启动文件及版权。涉及这些补充的 144 包使用 0.1.1，其他新包仍为 0.1.0；这是器件包修订，不改变 IDE 版本。

```powershell
python tools/Prepare-Arm32PublicRelease.py `
  --delivery artifacts/packs/ARM32-2026-09-30 `
  --existing-repository <MCUPacks仓库目录> `
  --output <新的公开暂存目录>
dotnet build tools/StudioX.Arm32PublicValidation -c Release `
  -p:StudioXDirectory="<现有IDE安装目录>" `
  --artifacts-path artifacts/build/arm32-public-validation
dotnet <公开验证器DLL路径> <公开暂存目录> <新的验证输出目录>
```

新公开包对应 2,777 次工程生成、891 组真实编译通过。公开修订只增加许可证及源文件注释，沿用基础包的固件编译证据，并记录基础归档哈希；不把文档重打包表述为重新编译全部型号。公开验证器额外使用 IDE 的真实 `GitHubPackSyncService` 和 `PackRepository` 验证 334 包的索引、下载哈希、完整导入和再次同步跳过行为。HTTP 从隔离的本地发布快照读取，不连接板卡或用户包仓库。

公开修订另抽测 APM32F103、LPC810、HC32F072、M030G 各 1 个型号，4 次真实编译通过；全部 272 个新公开包逐文件比较确认，除添加的许可、元数据和注释外，源码内容与设备配置保持原样。
