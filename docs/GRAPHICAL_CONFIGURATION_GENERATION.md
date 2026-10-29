# 图形配置的有限代码生成契约

2026-10-22 实现；插件声明可选 `mcuCodeGenerationV1`，要求 API 3、准确 `supportedProjects`、`mcuConfigurationPreview` 和时钟托管。它是当前试行视图的有限扩展，不代表已实现设计草案中的完整 `mcuConfigurationV1` 提供者协议。

图形界面上的“生成代码”即授权该次工程写入，不再要求独立预览和二次确认。“保存配置”也直接提交该次配置保存，不再转到插件管理页确认。新增可选 `mcuConfigurationSaveV1`（API 3、准确工程范围、时钟托管和配置视图），`PluginMcuSavePlan` 包含 `formatVersion`、`originalSha256`、`configuration`。MCP/插件直接调用 `action: generate/save` 只返回数据计划；桌面明确操作经应用服务核对当前插件、来源、受保护字段、磁盘摘要及脏缓冲区后提交。STC 插件不再申请通用 `project_edit_file` 工具。

## 数据与接入

SDK `PluginMcuGenerationPlan` 固定包含 `formatVersion: 1`、读取基线 `originalSha256`、候选 `configuration` 和 `files: [{path,content}]`。最多 32 个 UTF-8 文本文件、单文件 32,000 字符、总计 128 Ki 字符；禁止空字符。只允许 `src/generated/mcuconfig/<identifier>.c/.h` 和 `include/generated/mcuconfig/<identifier>.c/.h`，不接受插件提供的用户文件替换或任意 CMake 指令。文件基名支持 ASCII 大小写字母开头、后续字母/数字/下划线，最多 64 字符（例如 `Delay.c`）；目录和扩展名固定，大小写不同的同名输出仍视为重复，不允许子目录、尾随换行或路径穿越。

模板不可变的 `definitions.profileData.codeGeneration` 声明 `contractVersion: 1`、`mainPath: src/main.c`、`entryHeader` 和 `entryFunction`。应用服务核对来源、定义、profile、准确工程及候选时钟校验凭据；生成器不得改变这些不可变字段。生成后 `generation` 记录实际已启用插件的版本、内容指纹、profile 提供者 ID、输入摘要和每个实际输出的 SHA-256。输入采用现有时钟扩展的规范化投影：排除 `generation` 和校验自身的 `valid/inputSha256`。

`main.c` 模板预留 `/* StudioX MCU includes begin/end */` 和缩进四空格的 `/* StudioX MCU init begin/end */` 两组稳定标记。首次生成只允许空标记；之后头文件块仍需与上次接入完全相符。头文件由应用服务生成 include，上电接入直接调用 `sx_mcuconfig_init();`，不插入失败时的空循环。已有初始化块以完整、独占一行的原始调用开头时，整个初始化块原样保留；用户可在调用下一行追加 `USART1_printf(...)`、`Delay_ms(...)` 等业务代码，即使这些代码位于 init end 之前也不会阻止重生成或被覆盖。该规则只用于有生成凭据的工程，不自动接管首次生成时的已有代码。入口仍返回 `uint8_t`，供用户在自行编写的运行中调用处检查资源冲突；模板调用位于应用启动其它外设之前。上一版完整生成的 `if (!...()) { for (;;) { } }` 块可迁移为直接调用，混入用户语句的旧版块不自动迁移。接入标记前仍可放 C89 局部声明；用户内容、原有 BOM、编码和换行保留。缺失、重复的标记以及被注释、删除或修改的初始化调用拒绝写入，并明确提示位置；不搜索任意 main 函数插入。

根 CMake 必须是现有字面量源码登记解析器能够确定的顶层 `firmware` 目标，包含 `src/main.c`；条件、宏或变量源码列表拒绝自动改写。应用服务维护 `# StudioX MCU sources begin/end` 中的显式 `target_sources`。模板已有 `include` 头文件搜索目录。构建块外的用户配置保留。

## 文件所有权、事务与恢复

首次遇到同名文件即拒绝；重复生成只有与上次凭据 SHA-256 相同的输出才可更新或删除。手工改过、删除、只读或链接路径均不覆盖。所有受影响文件在规划时和逐文件写入前重新核对磁盘摘要；工程文本编辑器有未保存内容时拒绝生成。生成期间取消或出现脏缓冲区触发回滚。

应用服务先把完整前后字节备份到独立用户数据目录 `mcu-generation-transactions/<project-hash>/<operation-id>.json`，再在工程 `.studiox/mcu-generation.pending.json` 写入操作 ID。输出、main 接入、CMake 登记成功后，最后提交含生成凭据的配置文件。每个文件通过同目录临时文件和替换写入；这是带恢复记录的多文件事务，不声称多文件磁盘原子性。

失败时按反序恢复；重启后重新打开工程也恢复待处理事务。恢复先核对完整事务当前字节，外部修改导致保留备份和标记，禁止覆盖冲突。缺少原用户目录备份时明确报错。待恢复标记阻止托管构建与下载。成功后保留用户数据中的备份作为恢复证据，删除工程标记和临时文件。

源码同步完成后通过 `generationApplied` 通知插件读取新配置；输入摘要不一致时保留草稿。配置变化或生成文件摘要变化会让已托管时钟/构建变为不可用，要求重新生成，避免继续使用旧的 UART 波特率或 Timer 重装值。

## STC 试行范围

插件与配套器件包为 0.1.3，profile 为 2.2.0；仅支持 IAP15L2K61S2 / LQFP44 / bare / SDCC 4.5.0。新模板具有稳定接入点，旧工程不静默迁移。界面为横向“引脚与外设”“时钟树”两页，不提供代码预览页。

固定输出 `gpio.c/.h`、`timer.c/.h`、`uart.c/.h`、`mcuconfig.c/.h`。总入口先检查正在运行/占用的资源，再统一保护 EA，调用各模块，恢复原 EA；GPIO 先写锁存器再改模式，Timer0/1 保持停止，UART1 使用 Timer2 生成波特率。各模块初始化函数供总入口调用，不承诺运行中可单独重配，也不自动产生中断服务或开启外设中断。

51 提供者将非空引脚 `label` 生成为 `gpio.h` 中的 `#define LED P10` 一类位别名，不另建变量。`gpio.h` 引入器件头文件，`mcuconfig.h` 引入 `gpio.h`，因此既有 main 接入即可使用名字。别名覆盖已分配 GPIO 和外设引脚，名称区分大小写；生成前检查 C 标识符、重名及保留符号，旧配置仍可打开并修正。每次生成重建别名，改名、清空或释放引脚不残留旧宏；沿用现有输出所有权及事务规则。该规则在 STC 插件实现，宿主不引入 51 专有命名逻辑。

普通 STC 包 0.1.7 的 `stc89.h`、`stc12.h`、`stc15.h`、`stc8g.h`、`stc8h.h` 统一包含工程锁定 SDCC 的 `<stdint.h>`。保留全部型号、厂商寄存器声明、Mon51/RT51 文件及许可证；已有工程的 device 副本不在后台自动替换。编译验证覆盖软件生成与链接，不代表实板电气或时序验收。
