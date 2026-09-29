# AG32 基础引脚映射与自定义逻辑

AG32 的 C 代码操作内部 MCU GPIO、外设功能；`.ve` 决定这些信号与实际封装引脚之间的连接。只用 GPIO 也需要正确的映射镜像。StudioX 新建工程时默认生成 `logic/pins.ve`，并在工程清单的 `pinMapping` 字段记录基础映射工具与目标。可选的 `logic` 字段只代表自定义 Verilog。

当前按厂商 2026-06-01 手册支持七款精确型号：VF303 的 KCU6、CCT6、VCT6，VF407 的 RGT6、VGT6，以及 VH303RCT6、VH407VGT6。分别使用 QFN32、LQFP48/64/100 和 VH 专用逻辑目标，不能互换引脚表。四个分系列包、完整料号、容量、来源与历史型号边界见 [AG32 系列包说明](../examples/packs/agm.ag32-series/README.md)。

## 图形化引脚与时钟配置

点击左侧功能栏的 **AG32 引脚分配** 打开配置页。入口仅在已核实的 AG32 工程中显示；未打开工程或打开其它厂商工程时隐藏。基础映射页包含 ADC / DAC / 比较器、引脚分配、时钟 / 时序和约束文件页签。模拟硬核的启用、固定引脚、完整驱动和示例见 [AG32 外设说明](AG32_PERIPHERALS.md)。

1. 左键点击封装脚，在旁边弹出的菜单中选择内部 GPIO 或外设功能；菜单支持搜索，选择“清除分配（Reset_State）”可释放该脚。打开或关闭菜单本身不修改分配，选择新功能只替换当前物理脚的功能。封装图按顶视图、逆时针编号呈现，并标记一号脚。已分配引脚整条显示绿色，并在引脚上显示功能名；冲突涉及的引脚整条显示红色。固定脚与 VH 的保留资源以锁定的厂商转换器为准，不能分配。
2. 图形修改先保留为草稿；后台刷新不会丢弃草稿。同一功能分配给不同物理脚时，两条分配都保留，并提示涉及的功能与引脚，例如 `GPIO4_4` 同时分配给 `PIN_21` 和 `PIN_2`。用户须选择并移除多余的一条，才能保存。重复占脚、功能复用冲突与非法方向同样会拒绝保存，不覆盖已有分配。
3. 填写 HSECLK、SYSCLK、BUSCLK，单位 MHz。留空表示未显式设置，不将猜测值写入文件。系统与总线频率不得超过已核实的 248 MHz；PLL、分频与 VCO 组合交厂商转换器校验。
4. 可给引脚填写唯一英文名称（例如 `LED1`），GPIO 可选择输入或输出方向，以及无上下拉 / 上拉 / 下拉、推挽 / 开漏；输入模式禁用输出类型。点击 **保存并生成约束**，实际运行内置 VE 转换器，同步生成 VEX、`studiox-clocks.sdc`、`studiox-gpio.asf`、`device/studiox/StudioX_System.h` 和 `.c`，成功后更新原 `.ve`。保存全部或顶部编译也会先保存图形草稿。已打开的 VE 和系统文件立即同步，语言服务重新读取生成声明。
5. 在 **约束文件** 页查看实际生成文件。顶部编译进一步生成最终 ASF 与映射 BIN。图形保存和约束生成本身不调用 Supra，也不访问硬件。

转换结果存放在 `.build/ag32-pin-plan/`，仅保留最近成功的预览。原文件的 UTF-8 BOM、换行、注释及未修改的简单扩展配置保留。复杂共享功能、自定义逻辑或无法无损表达的 VE 显示只读诊断，可继续使用文本编辑；图形配置不重写这些内容。VE 编辑器与图形草稿同时有未保存改动时要求先处理冲突；磁盘文件或工程目标已变化时拒绝旧草稿提交。

内置及外部 Agent 可调用 `ag32_pin_plan_read` 获取真实引脚与功能目录，调用 `ag32_pin_plan_apply` 提交完整分配和时钟。读取免写授权；应用需要 FileWrite 授权，并核对源文件 SHA-256、工程身份及未保存修改。成功响应提供写入路径，供编辑器实时同步。

### 统一系统文件、命名引脚与延迟

新建最小工程的 `main.c` 仅引用 `StudioX_System.h` 并保留应用循环。系统头文件通过原厂 `alta.h` 统一引用全部 SDK 外设头文件，包括 GPIO、UART、SPI、I²C、CAN、DMA、定时器、USB、RTC、Flash 和看门狗等；这是头文件入口，不把厂商驱动源文件复制进主函数。

引脚名随映射保存在 VE 注释中；封装脚必须保留厂商语法中的下划线：

```text
GPIO4_4 PIN_2 #LED1
```

如果同时选择输出，生成 `GPIO4_4 PIN_2:OUTPUT #LED1`。原有说明注释保留在名称后，例如 `#LED1 # 板上指示灯`。名称使用唯一的 C 标识符，保留字、重复名称及 `LED1` / `LED1_Port` 等生成符号冲突会阻止保存。

GPIO 电气选项随 VE 保存，例如上拉开漏：

```text
GPIO4_4 PIN_2:OUTPUT #LED1 #@StudioX:GPIO pull=UP output=OPEN_DRAIN
```

未声明时使用无上下拉、推挽；回到这两个默认值时移除受管电气注释，保留名称和用户说明。`pull` 接受 `NONE / UP / DOWN`，`output` 接受 `PUSH_PULL / OPEN_DRAIN`；未知值或输入加开漏被拒绝，不静默改成默认值。图形界面与 MCP 使用相同规则。

这些属性位于 AG32 的逻辑 IO 单元，不是 MCU GPIO 寄存器。依据[厂商 GPIO 说明](https://www.ag32mcu.com/dev-docs/doc_ag32_driver_use/)及锁定工具中的 `alta_sim.v` IO 参数，生成 ASF 的 `CFG_KEEP`（00 无上下拉、10 上拉、01 下拉）和 `CFG_OPEN_DRAIN` 扩展约束。当前锁定工具在网表读入后未应用通用 `AUTO_OPEN_DRAIN_PINS` 设置，因此直接设置其 IO 原语参数，并检查最终布线网表确实匹配请求，才建立下载凭据。基础映射和自定义 FPGA 联合构建都会带入此 ASF。`StudioX_System.c` 仍只负责 GPIO 时钟、软件模式、方向和初始低电平，并注明对应电气配置；不生成不存在的 GPIO 上下拉 API。**电气设置须编译并下载映射镜像后生效**，保存配置本身不改变板上引脚。

```c
#include "StudioX_System.h"

int main(void)
{
    for (;;)
    {
        GPIO_SetHigh(LED1_Port, LED1_Bit);
        Delay_ms(500);

        GPIO_SetLow(LED1_Port, LED1_Bit);
        Delay_ms(500);
    }
}
```

完整示例见 [main-named-led.c](../examples/packs/agm.ag32-series/main-named-led.c)，需在界面中把 LED1 明确配置为 **输出（初始低）**。`LED1_Port` 为内部 `GPIO4`，`LED1_Bit` 为位掩码 `GPIO_BIT4`，`LED1_Pin` 为封装脚号 `2`；也可使用 `LED1.Port`、`LED1.Bit`、`LED1.Pin`。物理脚号不参与 GPIO 位掩码计算。只分配功能而未选择方向时，不自动驱动引脚；输出方向的时钟使能、软件 GPIO 模式与低电平初值放在系统 `.c` 中。

`StudioX_SystemInit()` 通过已锁定 SDK 支持的启动构造器，在 `main()` 前执行。未显式配置 SYSCLK 时保留模板的内部 HSI 行为；要配置外部晶振和 PLL，须同时填写真实 HSECLK 和 SYSCLK，BUSCLK 由实际转换结果计算分频。当前自动初始化支持 HSE→PLL 与整数总线分频。先设置 Flash 时钟分频，再提高系统频率；HSE 或 PLL 等待超过 100 ms 时回退 HSI，并通过 `StudioX_SystemStatus` 报告原因。模板不会把转换器的默认外部晶振值当作已确认的板级接线。

`Delay_us(uint32_t)`、`Delay_ms(uint32_t)` 和 `Delay_1ms()` 使用 RISC-V 硬件 `mcycle`，根据当前时钟源的频率换算，不占用 `mtime` 中断或外设定时器。采用 RV32 高/低/高一致性读取、64 位乘法、向上取整及无符号差值，支持计数回绕和长毫秒参数。它们是阻塞延迟；中断会延长实际等待，晶振精度和调用开销仍影响短延迟，不能据编译或模拟结果宣称实板微秒精度。不要在等待中切换时钟或在 ISR 中长时间阻塞。

已有工程通过保存引脚配置接入系统文件和受管器件 CMake，用户 `main.c`、根 CMake 和厂商 SDK 保持原样；应用文件需引用 `StudioX_System.h`。已有代码若自行改变时钟，会覆盖启动配置，延迟按当前时钟源计算。文本编辑 `.ve` 后，IDE 编译 MCU 前重新运行转换器并更新系统文件。系统 `.h/.c` 在 IDE 中只读；外部修改或同名文件占用会明确拒绝覆盖。取消或写入失败回滚本次文件修改，名称清除或重命名同步移除旧别名。

2026-09-28 系统层验证：`artifacts/validation/ag32-system-complete/result.txt` 的 61 项检查通过，覆盖七款精确型号的实际 RISC-V GCC 编译链接、实际 clangd 成员与延迟补全、名称冲突、文本改名、用户主函数与外部系统文件修改保护；宿主寄存器模型另覆盖 RV32 / 64 位回绕、非整数 MHz、零延迟、大毫秒参数和启动超时。`artifacts/validation/ag32-system-pin-plan-final/result.json` 的 73 项引脚规划回归通过；`artifacts/validation/ag32-system-ui-final/result.json` 的 58 项 WPF 检查通过，命名界面截图为同目录 `named-pin-system.png`。基础构建日志 `artifacts/validation/ag32-system-build-release-check.txt`；未连接或烧录硬件，未测量实板延迟精度。

### 基础映射的时序约束与报告

基础映射从厂商 `pins.hx` 读取频率，并从 `pins.vx` 核对实际连接，再生成精确端点的 `set_max_delay`。GPIO 输入、输出数据和输出使能采用一个 BUS 周期的片内传播预算；SYS 分配及固定 PLL 反馈连接采用一个最快已声明系统源时钟周期，PLL→BUS 分配采用 BUS 周期。例如 200/100 MHz 对应 5/10 ns，160/80 MHz 对应 6.25/12.5 ns。BUS 未启用时按系统频率计算。

这是 StudioX 的默认片内布线预算，不是厂商给出的外部接口时序规格。不会自动生成全局 `set_false_path`，也不猜测 SPI、SDRAM 或外部采样器件的建立/保持时间；尚未建模的连接仍会报告未覆盖。自定义 Verilog/IP 模式继续使用工程提供的 SDC，不套用基础 GPIO 的预算。可参考[厂商 Supra 手册](https://www.ag32mcu.com/wp-content/uploads/2025/06/MANUAL_Supra_6.2.pdf)关于完整时序约束和 IO 时序的说明。

Supra 完成后，IDE 检查实际建立/保持余量；出现负余量时拒绝成功凭据并移除候选映射 BIN，保留原始报告。成功报告导出到 `.build/ag32-mapping/studiox-timing.json`、`coverage.rpt`、`setup_summary.rpt`、`hold_summary.rpt`；覆盖率、未覆盖端点、最小余量和报告散列均可核对，报告内容与下载凭据绑定。无可分析保持路径会明确显示，不等同于保持时序通过。已有工程下次编译自动更新，无需编辑受管 SDC。

2026-09-28 的 LED1 输出示例重新执行厂商 VE 转换与 Supra，覆盖 10/10 条连接，0 警告，最小建立余量 2.030 ns；见 `artifacts/validation/ag32-timing-existing-preview.json.build.log` 及示例工程时序报告。独立回归覆盖 GPIO 双向路径、PIN2/PIN21、200/100 和 160/80 MHz、报告与预算篡改，以及真实 Supra 负余量拒绝。结果位于 `artifacts/validation/ag32-timing-mapping-complete`，不代表实板测量或外部接口验收。

## 基础 MCU 引脚映射

基础映射使用内置的 AGM VE 转换器和 Supra，从厂商默认 MCU 逻辑网表生成独立 BIN，**不需要外部 Quartus，也不需要编写 Verilog**。MCU GCC、基础映射工具各自锁定：`agm.agrv` 用于 C 固件，`agm.pin-mapping/1.0.0`、编译器标识 `agm.ve` 用于 `.ve`。内置工具保留厂商版本来源、许可证与 SHA-256，不以用户脚本替代默认网表。

普通工程的 `logic/pins.ve` 是可编辑配置文件，初始内容只有说明与注释示例，不猜测开发板引脚或晶振。用户按原理图填写 MCU 功能与封装引脚，例如：

```text
# C 中仍操作 GPIO4 / GPIO_BIT4；此处根据实际接线选一个封装脚。
GPIO4_4 PIN_21
```

将该行改为 `GPIO4_4 PIN_2` 会改变物理连接，C 代码不用随之改成另一个内部 GPIO。应确认二号脚是该封装可使用的用户 IO、电气连接正确，而且实际观察电路已接到二号脚。固定电源、地、配置等引脚不能当作通用 IO。厂商允许部分输入、输出外设共享一个功能行，但 GPIO 与该行的外设有复用关系，必须同时核对固件中的 AF 设置。

StudioX 基础模式已经接入普通 **编译 / 下载**，无需来回复制配置到 PlatformIO：

1. 新建已支持型号的普通工程后打开 `logic/pins.ve`；已有工程先点击左侧功能栏的 **AG32 引脚分配**，在页面选择 **启用基础映射**，原 `.ve` 会保留。
2. 在同一页面选择 **配置 Supra 许可**，导入本人已有、适用于本机的厂商 `license.txt`。无需安装外部 Quartus；许可有效性由 Supra 实际编译检查。
3. 编写 C 代码，按实际连线填写 `.ve`，保存全部文件。未启用模拟 IP 且没有任何数字映射的骨架不能作为可下载映射；纯 ADC / DAC / CMP 配置允许没有数字 IO。不能在基础模式加入任意自定义 IP 连线或 `ASSIGN` 逻辑。
4. 按 **F7 / 顶部编译**，IDE 先编译 MCU 固件，再用内置转换器和 Supra 生成 `.build/ag32-mapping/pins.bin`。两步都成功才建立完整 MCU 构建凭据。
5. 点击顶部 **下载**。IDE 保存并编译后，直接写入 MCU 应用和映射镜像，分别回读校验，最后复位运行；当前编辑标签保持不变。目标、烧录器、两段镜像的地址、大小、SHA-256 与布局散列写入构建输出，不再重复弹出确认窗口。停止操作、关闭主窗口或工程上下文失效仍会取消下载；源码或镜像变化会拒绝旧下载计划。
6. 观察实际连接到所选封装脚的 LED 或外设。开始硬件调试时，IDE 另外校验板上映射 BIN 与当前 `.ve` 构建产物一致；不会在调试附加时隐式下载。

映射镜像仍是独立产物；如果绕过这个双镜像流程、只用外部工具下载 MCU BIN，不会更新旧映射。每次 `.ve` 修改都应重新构建，源码、工具和镜像均以内容散列校验，不用文件时间戳证明旧镜像有效。

### 烧录器选择

AG32VF303CCT6 可在工具栏选择 **DAP-Link（CMSIS-DAP）** 或 **J-Link（V9 及以上）**，MCU 固件和 VE 映射共用所选探针与序列号，默认速度 1000 kHz。旧工程中的 `agm-blaster` 设置按 CMSIS-DAP 兼容读取，不改写工程的器件包锁定。

两种探针都使用内置 AGM 专用 OpenOCD 的 SWD 通路。[AGM 指南](https://www.ag32mcu.com/dev-docs/doc_ag32_vscode_start/) 提供两者的下载与调试步骤；[SEGGER V9 规格](https://kb.segger.com/J-Link_BASE_V9) 确认其具备 SWD 接口。J-Link 在 Windows 上需要可供 OpenOCD 访问的 USB 驱动，IDE 不自动替换驱动。当前 J-Link 完成厂商依据核对和离线目标脚本解析，实板验收仍只有已记录的 CMSIS-DAP 路径。其它六款 AG32 的下载身份限制保持有效。

AG32VF303CCT6 新系列包使用 `debug/ag32vf303cct6.cfg`，旧包使用 `debug/ag32vf303.cfg`。下载和调试按包声明的路径读取，只接受这两个已验证名称；双镜像流程继续逐字核对内置目标检查脚本，缺失或被修改时拒绝执行，不回退到另一个路径。2026-09-28 路径兼容的 54 项回归见 `artifacts/validation/ag32-target-name-compat/result.txt`；原 `ag32_led_blink` 工程的真实构建产物已通过下载预览与调试准备，结果为 `artifacts/validation/ag32-target-name-existing-preview.json`，未访问硬件。

### 本机 Supra 许可

Supra 的 `license.txt` 是厂商节点授权数据，**不随 StudioX 工具发行、不放进工程或 GitHub，也不记录许可内容**。导入后存储在当前用户的 `%LOCALAPPDATA%/MCUStudioX/licenses/ag32-pin-mapping/`，构建时只在该私有目录的临时运行环境组合许可与已锁定工具资源，结束后清理临时副本。发行包只带工具、资源与开源许可声明；换一台电脑须配置那台电脑自己的有效厂商许可。

已有 AG32 工程不会自动改写。可显式启用基础映射：应用服务 `ProjectService.EnableAg32PinMappingAsync` 验证当前器件元数据后保存 `pinMapping`。原来存在的 `logic/pins.ve` 保留全部原始字节；不存在时才创建注释骨架。无需重新创建工程、覆盖 SDK 或复制原来的 `platformio.ini`。已有自定义 Verilog 模式不能由此操作隐式切换到基础默认网表。

### 时钟配置

`HSECLK`、`SYSCLK`、`BUSCLK` 等字段交给已锁定的厂商 VE 转换器处理，编辑器原样保存；不存在字段时不由 IDE 填入猜测值。当前已锁定转换器默认 HSE 为 8 MHz、SYSCLK 为 100 MHz，未指定 BUSCLK 时使用 SYSCLK；这是厂商源码默认，不证明开发板采用了 8 MHz 晶振。外部晶振值和频率组合需要按实际板卡与当前 SDK 支持范围填写；BUSCLK 必须能由 SYSCLK 分频得到，PLL 输入与输出必须能满足转换器的 VCO 规则，非法组合保留厂商原始诊断并停止构建。接入 `StudioX_System.h/.c` 的工程从实际转换结果同步频率与总线分频；尚未接入的旧工程仍须自行保持 MCU 时钟代码与映射镜像一致。

## 自定义 Verilog 联合构建

自定义模式已经接入顶部 **编译 / F7**：MCU GCC 编译 → VE 生成顶层 → 内置 AGM 原生 mapper 综合 → Supra 布局布线 → 时序报告与位流。整个流程不需要外部 Quartus；Supra 仍使用本机用户目录中的有效厂商许可。只有两部分都成功才建立完整构建凭据。

1. 创建 AG32 工程并勾选 Verilog 逻辑模式，编辑 `logic/user_logic.v` 与 `logic/pins.ve`。VE 可以定义 MCU 功能到逻辑信号、逻辑信号到封装脚等连接；模板不猜测实际接线。
2. 点击左侧 **AG32 引脚分配**，打开 **联合构建与仿真**。联合构建配置包含设计源文件、包含目录、宏及附加 SDC，均为工程相对路径。配置保存到 `.studiox/ag32-logic-build.json`。testbench 不应加入硬件综合源文件。
3. 配置本机 Supra 许可后按 F7。每次在 `.build/ag32-logic/<运行编号>/` 使用独立源码快照，生成顶层、接口模板、VQM、布局后的 Verilog、未压缩 `pins.bin` 和完整工具日志。VE 的接口变化后须按生成模板合并用户模块端口；构建不覆盖原始 Verilog。
4. 时钟 SDC 从实际 VE 转换结果生成，再合并配置中的附加 SDC。`setup.rpt`、`hold.rpt`、`fmax.rpt`、`coverage.rpt` 保存厂商原始静态时序结果，在联合构建配置中选择报告并点击“打开时序报告”查看。构建成功表示位流生成成功，不自动证明所有时序路径已约束或收敛；须同时检查违例及覆盖率。
5. 顶部下载重新联合构建，在构建输出记录两段镜像的地址、大小、SHA-256 和布局散列，随后直接分别写入、分别校验，再复位运行。源码、SDC、工具锁定或产物变化会拒绝旧下载凭据；调试附加同样校验逻辑镜像，不隐式下载。

构建分析器在 MCU 存储占用下显示 **FPGA → 逻辑单元 (LE)** 的已用数量、总量和百分比。数据读取本次成功联合构建的 Supra 日志，优先采用 `Route Design Statistics / Logic Slices`，旧日志可回退到布局或打包阶段的逻辑统计；容量也来自同一报告，不按型号猜测。LUT 和寄存器可能共享单元，不能相加为 LE；位流大小也不代表 LE 占用。只有基础 GPIO/时钟映射的设计可能占用 0 个逻辑单元。缺少有效统计时显示未知；重新编译失败会清除旧统计，重开工程则保留上次成功构建结果。

256 KiB Flash 型号保留前 156 KiB 给 MCU，逻辑地址为 `0x80027000`；1 MiB 型号保留前 924 KiB，逻辑地址为 `0x800E7000`。末尾 100 KiB 保留未压缩逻辑配置；禁止由工程设置改变此布局或选项字节。当前实板下载身份检查只开放已验证的 AG32VF303CCT6，其它型号不能据此宣称实板验收。

## RTL 波形仿真

在 **联合构建与仿真 → 工程配置与 testbench → RTL 仿真** 中指定设计源文件、testbench 文件、顶层模块、包含目录、宏、最大模拟时间（ns）与进程超时（秒）。点击 **保存并运行 RTL 仿真**，内置 Icarus Verilog 编译并执行 testbench，界面显示真实 VCD 波形。

- **新建 testbench 模板**只创建新文件，不覆盖现有代码。模板包含一个待替换的失败断言；用户需要实例化 DUT、连接端口、编写时钟/复位/数据激励及检查条件。所有断言完成后调用 `$finish`。
- 支持数字标量、总线、层级名、未知态 X 和高阻态 Z；可筛选、缩放及点击定位时间光标。按住左键可连续拖动蓝色光标，时间和各信号值同步刷新，松开后保留位置；拖出范围时限制在起止时间。密集跳变聚合绘制，放大后展开；原始 VCD 保留完整事件。
- 支持 `#delay` 与当前 Icarus 前端可执行的 Verilog / SystemVerilog。需要厂商 IP 仿真模型时，须自行加入设计依赖；不会把缺失模型当作已验证逻辑。
- `$fatal`、编译失败、超时及运行中输入变化都返回失败。顶部停止取消进程。达到模拟时间上限会明确警告，不能把它当作 testbench 已执行全部断言。
- `.studiox/hdl-simulation.json` 保存配置；`.build/hdl-simulation/<运行编号>/` 保存输入快照、编译产物、`simulation.log`、`wave.vcd` 和结果索引。编辑源码或配置后，界面标记历史波形。
- VCD 最大 32 MiB、2048 条信号、一百万次变化；界面最多显示 128 条匹配信号。过限返回明确诊断，可缩短模拟时间或缩小 testbench。

**本次实现 RTL 事件仿真和布局布线后的静态时序报告，尚未实现带 SDF 的布局后延时仿真。** 目前内置 Supra 的已核实命令不提供 `write_sdf`；不能用 RTL 的 `#delay` 或静态 Fmax 代替物理时序仿真。后续需要完成厂商延时导出、对应仿真单元库和 SDF 反标校验。

Agent 共用应用服务：`project_build` 联合编译 MCU 与 FPGA，`ag32_logic_workflow_settings` 读取或保存配置（写入需要 FileWrite 授权），`ag32_logic_simulate` 执行 RTL testbench（Build 授权）并返回 VCD、原始日志、信号列表和警告。仿真不建立硬件会话。

开发环境使用 `tools/Prepare-HdlWorkflowRuntime.ps1 -SupraDirectory <已有 Supra> -IcarusDirectory <已有 Icarus>` 整理已核实工具；最终程序使用内置工具集，不从用户 PATH 查找。mapper、仿真器、资源文件均有 SHA-256 索引，Supra 私人许可不会进入工具树。

## 验收边界

- 七款型号的新模板默认生成基础 `logic/pins.ve`，不猜测板级映射，不要求 Quartus。自定义 Verilog 可选项默认关闭。
- 旧工程不自动生成或启用映射；显式启用保留既有 `.ve` 的 BOM、编码、换行、注释和全部配置，不覆盖 MCU 源码与 SDK。
- 启用后，工程元数据持久记录该选择；重新打开工程仍能找到 `.ve` 与 Verilog 文件。含空格和中文的工程路径应可用。
- 构建使用内置工具及内容指纹；厂商失败、未分配 IO、镜像越界会拒绝下载凭据。MCU 编译成功不能替代逻辑构建成功。
- 静态检查使用准确封装范围及厂商可分配脚表；不能将 48 脚器件的编号沿用到 32、64 或 100 脚器件。文本模式的复杂复用仍需人工核对；图形模式只接受可无损表达和验证的基础分配。
- `.ve`、器件、工具或固件改动后，旧构建凭据失效。内容散列用于核对镜像与源码，文件时间戳不能代替这一检查。
- 当前仅 AG32VF303CCT6 保留已核实的硬件下载和调试身份检查。新增六款可以创建工程、编译和生成图形约束，硬件入口在完成各自实板身份验证前拒绝操作，不复用 CCT6 的探针配置。
- 软件构建成功不代表实板验收。实际下载、引脚电气行为和 MCU 与逻辑通信需要在目标板上单独验证。

离线工程验收可运行 `dotnet run --project tools/StudioX.DebugValidation -- --ag32-logic <AG32 包文件> <新的输出目录>`。它创建默认基础映射和自定义模式两种工程，检查旧工程无隐式启用、显式启用保留原字节、幂等和取消、LQFP48 引脚边界及复用提示、非法器件/厂商/工具组合与被篡改的 100 脚目标；不会启动逻辑工具或访问硬件。

### VE 编辑器回归

`.ve` 中的 `SYSCLK`、`BUSCLK`、`HSECLK` 和引脚映射必须作为原始配置文本保留。语法高亮不能重写配置、调整频率或重新生成逻辑文件。AvalonEdit 的 XSHD 正则使用 `IgnorePatternWhitespace`；注释前缀必须写为 `\#`。未转义的 `#` 会被当作正则注释，产生零长度匹配，并在实际绘制文本时抛出异常。仅加载语法定义无法发现该错误。

`dotnet run --project tools/StudioX.DesktopArchitectureChecks --no-restore` 会执行真实逐行高亮，覆盖深浅主题、空行、中文与行尾注释、时钟字段和引脚方向。编辑器在高亮失败时延后切换为纯文本，并记录完整异常；不在 WPF 正在遍历着色器时修改集合，也不全局忽略界面异常。

实际窗口回归可运行 `MCU StudioX.exe --preview-ve <输出目录> <现有工程目录> logic/pins.ve`。该模式只复制必要的小型工程文件，不复制 SDK 或编译链。它验证真实 VE 文本的深浅主题渲染、C/Verilog/VE 标签切换、编辑保存和重新打开，比较副本的编码、BOM、换行及全部配置字节，并核对真实文件的 SHA-256 未变。所有写操作发生在输出目录的夹具中，不启动逻辑工具或访问硬件。

图形与 MCP 离线验收入口为 `tools/StudioX.Ag32PinPlanningValidation`：参数是工具集根目录、新输出目录和真实器件包 `manifest.json`。完整检查使用含 CCT6 的 VF303 包；其它包增加 `--profiles-only`，逐型号运行实际转换器。真实窗口入口为 `MCU StudioX.exe --preview-ag32-mapping <新输出目录> <CCT6 工程目录>`，覆盖图形保存、冲突着色与提示、保存全部、SDC 打开和编辑同步。所有测试写入独立夹具，均不连接硬件。

## Verilog 电路图预览

AG32 的 **MCU+FPGA 自定义逻辑工程** 可从左侧 **AG32 引脚分配 → Verilog 电路图** 进入。点击 **生成电路图**，保存编辑器内容后，用内置 Yosys 0.61 处理实际 Verilog，显示优化后的 RTL 网表。普通 MCU 基础映射工程和其它系列隐藏此入口。

- 支持组合逻辑、算术单元、MUX、寄存器、锁存器、存储器和子模块；总线按位连接，点击信号线可检查切片与扇出。
- 双击子模块查看内部电路，使用“返回上层”或模块列表导航；点击元件后“定位源码”跳到真实 Verilog 行。优化掉的逻辑不会在网表中出现。
- Ctrl + 滚轮或工具栏缩放，滚动条移动视图；当前模块可导出为独立 SVG。
- 配置存放在 `.studiox/hdl-schematic.json`，可指定源文件、包含目录、宏、顶层模块和展开子模块。首次进入自动发现入口所在目录及子目录的 `.v/.sv`，请移除 testbench 并补齐实际依赖；来源路径均相对当前工程，不保存开发者工具路径。
- `include` 与 `$readmemh/$readmemb` 的字面依赖使用快照内的相对路径；外部依赖应先放入工程，再配置包含目录。绝对路径及 `../` 引用明确报错。空格目录通过快照别名处理；支持的附加输入扩展名是 `.vh/.svh/.mem/.hex/.mif/.dat`。
- 每次生成使用 `.build/hdl-schematic/<运行编号>/source` 快照，保存 `netlist.json`、`report.json`、`preview.ys`、`yosys.log` 和 `process.log`。记录输入 SHA-256、工具版本和结果时效；源码或配置变化提示重新生成，失败会清空当前图并保留原始诊断，可用顶部停止按钮取消。
- 显式声明的厂商黑盒显示端口与黑盒标记，不能据此推断内部电路；未知模块直接报错。综合警告在界面提示，详细内容保留在日志。

该入口提供 **RTL 电路结构预览**，不生成 AGM 位流，不估算物理 LE 占用，不执行布局布线、时序分析或波形仿真。Verilog-2005 与部分 SystemVerilog 以所锁定 Yosys 前端的实际支持为准；完整 SystemVerilog、VHDL 和厂商加密 IP 不在本次范围内。实际 FPGA 镜像由顶部联合构建的厂商原生工具流程生成。

Yosys 运行时位于 `runtime/hdl/yosys`，发行构建自动包含它；预览无需用户安装 Quartus、Node 或配置 Supra 许可。开发环境用 `tools/Prepare-HdlRuntime.ps1 -YosysExecutable <已有 yosys.exe>` 从已核验本机分发包整理，保留 ISC 许可、来源、版本与 SHA-256。IDE 启动综合前校验哈希，不复制厂商私人许可。参考 [Digital IDE 网表功能](https://nc-ai.cn/en/article/x29n1v76/) 与 [Yosys 上游](https://github.com/YosysHQ/yosys)，图形显示由 StudioX 的 C# / WPF 实现。

Agent 共用应用服务：`ag32_logic_schematic_settings` 只读配置，`ag32_logic_schematic_generate` 通过已有 Build 授权流程生成网表和 SVG，返回模块、端口、警告及原始日志路径；不获取下载授权。

软件验证：`dotnet run --project tools/StudioX.HdlValidation -- <源码根目录> <验证输出目录> <现有 CCT6 工程>` 创建独立测试夹具；`MCU StudioX.exe --preview-hdl <界面输出目录> <上述 fixture>` 验证真实窗口、综合按钮、层级、源码跳转和深浅主题。测试不连接硬件。

## 厂商资料

2026-09-28 已使用 AG32VF303CCT6 实板完成同源 Verilog 的 MCU↔FPGA 内部回环，并在 IDE 生成对应 RTL 图。硬件位流由本机 Supra 2026.03 原生流程生成，本次没有调用 Quartus；完整证据及未覆盖范围见 [实板验收记录](AG32-LOGIC-HARDWARE-ACCEPTANCE-20260928.md)。后续已接入 IDE 联合构建、双镜像下载及 RTL 仿真，验收见 [联合工作流记录](AG32-HDL-WORKFLOW-ACCEPTANCE-20260928.md)。

- [AG32 参考手册，2026-06-01](https://www.agm-micro.com/upload/userfiles/files/AG32%20MCU%20Reference%20Manual%2820260601%E4%BF%AE%E8%AE%A2%E7%89%88%EF%BC%89.pdf)：当前七款订货型号、容量和封装。
- [AG32VH 系列应用指南](https://www.ag32mcu.com/wp-content/uploads/2025/06/MANUAL_AG32VH_HyperRAM.pdf)：VH 逻辑目标、HyperRAM 和保留引脚。
- [AG32 下 FPGA/CPLD 使用入门](https://www.ag32mcu.com/dev-docs/doc_ag32_fpga_cpld_start/)：MCU 与逻辑独立构建和下载、Quartus II Full / Supra 流程。
- [AGRV2K 逻辑设置](https://www.ag32mcu.com/dev-docs/doc_ag32_logic_setup/)：48 脚器件选择、`.ve` 映射、默认逻辑区与自定义模块生成。

基础映射的内置工具来自本机 AGM SDK 的 `tool-agrv_logic`，运行原始转换和 Supra 编译，保留原始厂商诊断。自定义 Quartus II 综合与任意板级连线不属于基础映射的软件验收；实际下载、电气行为和用户外设配置仍须在准确目标板上验证。
