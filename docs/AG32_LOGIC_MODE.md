# AG32 Verilog 逻辑模式

AG32VF303CCT6 同时包含 MCU 和约 2K 逻辑单元（LE）。创建该型号工程时，可选择是否启用 Verilog 逻辑模式。默认关闭；只开发 MCU 固件时无需安装逻辑工具。该选项针对本包的 LQFP48 器件，逻辑器件型号为 `AGRV2KL48`，不能沿用厂商示例中常见的 `AGRV2KL100` 引脚表。

## 所需软件与产物

启用此模式需要用户另行准备 **Quartus II Full 和 AGM Supra**。厂商推荐 Quartus II 64-Bit 13.0.1 Full；Lite 版不适用于其流程，还需要安装相应器件库。Quartus II 将 Verilog 设计编译、转换为 `.vo`；Supra 再将其转换、编译为逻辑 `.bin`。StudioX 的内置 AgRV GCC 只编译 MCU 固件，不能代替这两套逻辑软件。软件安装、许可和器件库由用户负责；工程文件不记录开发者电脑上的绝对安装路径。IDE 可从 `PATH` 或 `STUDIOX_AG32_QUARTUS`、`STUDIOX_AG32_SUPRA` 查找可执行文件；查找到文件不代表授权可用。

逻辑镜像和 MCU 固件是两个独立产物，必须分别构建、分别下载。StudioX 普通“编译 / 下载固件”操作只处理 MCU 应用，不会把 Verilog 当作 C 源码编译，也不会把逻辑镜像合并进 MCU 固件。当前“AG32 逻辑构建指引”和“AG32 逻辑下载指引”只检查工程、工具位置、引脚编号及镜像文件并展示步骤；**StudioX 尚无 Prepare LOGIC 任务，不会自动运行 Quartus II / Supra，也不会自动烧录逻辑区**。这些步骤需在 AGM 配套工程和工具中完成。

本包的 256 KiB Flash 为 MCU 应用保留前 156 KiB，末尾 100 KiB 留给未压缩逻辑配置，起始地址为 `0x80027000`。2K LE 是可用逻辑资源数量，不是逻辑镜像或 RAM 的字节数。当前包不支持改变逻辑地址、开启压缩或将逻辑嵌入 MCU 镜像。

## 工作步骤

1. 在新建工程时选中 AG32VF303CCT6，再勾选 Verilog 逻辑模式。未勾选的工程保持 MCU 开发流程；已有工程不会被自动切换。启用后生成 `logic/user_logic.v`、`logic/pins.ve` 和目录内说明。
2. 先编辑 `logic/pins.ve`。按开发板原理图指定 MCU 功能、逻辑信号与 LQFP48 引脚的关系；同一引脚出现多种映射时，需按 AGM 复用规则核对。模板故意不预设物理引脚。
3. 在 **AGM AgRV SDK / PlatformIO 配套工程**中同步 `logic/pins.ve`，再运行厂商的 **Prepare LOGIC**；StudioX 工程没有该任务。本机 AGM SDK 的 `platformio.ini` 使用以下字段。示例假设将映射文件复制到配套工程根目录，名为 `pins.ve`：

   ```ini
   [setup_logic]
   logic_ve = pins.ve
   logic_device = AGRV2KL48
   ip_name = user_logic
   logic_dir = logic
   ```

   每次修改 StudioX 工程中的 `logic/pins.ve` 后，都要同步配套工程的 `pins.ve`。较旧的 AGM 网页将 `logic_ve`、`logic_device` 分别写作 `board_logic.ve`、`board_logic.device`；应以已安装 SDK 的配置格式为准。
4. 核对 Prepare LOGIC 生成的顶层与自定义模块接口，再编辑或合并 `logic/user_logic.v` 的端口和实现。自动生成的顶层文件会随 `.ve` 改动重新生成，不要手工维护。`.ve` 变化后重新运行 Prepare LOGIC，并把新增接口合并到用户模块；厂商工具可能生成 `_tmpl.v` 供合并。
5. 用 Quartus II Full 对厂商工程中的 Verilog 编译和转换，得到 `.vo`；再用 Supra 将 `.vo` 编译为逻辑 `.bin`。将配套工程生成的 `pins.bin` 复制到 StudioX 工程的 `logic/pins.bin`，核对容量和诊断。
6. 按厂商的独立 Upload LOGIC 流程将逻辑镜像写入逻辑区域，并在连接目标板前核对器件、封装、引脚和 Flash 布局。MCU 固件仍通过 StudioX 原有构建和下载操作处理。

`.ve` 有三类映射，分别是 MCU 功能到封装引脚、逻辑信号到封装引脚、MCU 功能到逻辑信号。逻辑信号到外部引脚的方向为 `INPUT`、`OUTPUT` 或 `INOUT`。例如 `LED_OUT PIN_32:OUTPUT` 仅说明文件格式，`PIN_32` 是否可用必须以实际 LQFP48 板卡和器件资料核对，不应直接复制到项目。

## 验收边界

- 创建工程时，只有 AG32VF303CCT6 显示该模式；默认关闭。未启用时不生成逻辑子工程，也不要求 Quartus II / Supra。
- 启用后，工程元数据持久记录该选择；重新打开工程仍能找到 `.ve` 与 Verilog 文件。含空格和中文的工程路径应可用。
- IDE 指引仅检查可执行文件是否存在，不能证明工具版本、器件库或许可可用。厂商构建失败时应查看其原始诊断，不能把 MCU 构建成功当作逻辑构建成功。
- 静态检查拒绝超出 `PIN_1`–`PIN_48` 的编号；重复映射会提示人工核对，因为部分输入/输出复用可能合法。仍需对照板级资料核查固定功能、电气约束及复用冲突。
- `.ve` 或 Verilog 改动后，旧逻辑镜像的修改时间会触发重新构建提示，但时间戳不足以证明镜像与源码一致。下载前需确认目标型号、逻辑地址 `0x80027000` 和镜像不越过 `0x80040000`，保留 MCU 应用区与选项字节。
- 软件构建成功不代表实板验收。实际下载、引脚电气行为和 MCU 与逻辑通信需要在目标板上单独验证。

离线工程验收可运行 `dotnet run --project tools/StudioX.DebugValidation -- --ag32-logic <AG32 包文件> <新的输出目录>`。它创建默认和启用模式两种工程，重新读取清单，检查 LQFP48 引脚边界和复用提示、非法器件组合及被篡改的 100 脚目标；不会启动逻辑工具或访问硬件。

### VE 编辑器回归

`.ve` 中的 `SYSCLK`、`BUSCLK`、`HSECLK` 和引脚映射必须作为原始配置文本保留。语法高亮不能重写配置、调整频率或重新生成逻辑文件。AvalonEdit 的 XSHD 正则使用 `IgnorePatternWhitespace`；注释前缀必须写为 `\#`。未转义的 `#` 会被当作正则注释，产生零长度匹配，并在实际绘制文本时抛出异常。仅加载语法定义无法发现该错误。

`dotnet run --project tools/StudioX.DesktopArchitectureChecks --no-restore` 会执行真实逐行高亮，覆盖深浅主题、空行、中文与行尾注释、时钟字段和引脚方向。编辑器在高亮失败时延后切换为纯文本，并记录完整异常；不在 WPF 正在遍历着色器时修改集合，也不全局忽略界面异常。

实际窗口回归可运行 `MCU StudioX.exe --preview-ve <输出目录> <现有工程目录> logic/pins.ve`。该模式只复制必要的小型工程文件，不复制 SDK 或编译链。它验证真实 VE 文本的深浅主题渲染、C/Verilog/VE 标签切换、编辑保存和重新打开，比较副本的编码、BOM、换行及全部配置字节，并核对真实文件的 SHA-256 未变。所有写操作发生在输出目录的夹具中，不启动逻辑工具或访问硬件。

## 厂商资料

- [AG32 下 FPGA/CPLD 使用入门](https://www.ag32mcu.com/dev-docs/doc_ag32_fpga_cpld_start/)：MCU 与逻辑独立构建和下载、Quartus II Full / Supra 流程。
- [AGRV2K 逻辑设置](https://www.ag32mcu.com/dev-docs/doc_ag32_logic_setup/)：48 脚器件选择、`.ve` 映射、默认逻辑区与自定义模块生成。

本机 AGM SDK 中可找到 `tool-agrv_logic` 的 Supra、转换及下载工具；仅找到文件不代表已验证其授权或完整综合。当前开发环境尚未完成 Quartus II 加 Supra 的端到端逻辑构建和目标板下载验收。
