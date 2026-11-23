# AG32 外设使用与离线验收

统一入口为 `#include "StudioX_System.h"`。IDE 会接入完整原厂 SDK 驱动实现，包含 GPIO、UART、SPI、I²C、CAN、DMA、通用/基础定时器、RTC、USB、MAC、CRC、FCB、Flash、系统时钟及看门狗；不再只有头文件声明。普通 MCU 和 FreeRTOS 模板使用同一系统层，应用代码留在 `src/`。

## ADC / DAC / CMP

普通 MCU / FreeRTOS 工程打开左侧 **AG32 引脚映射 → 引脚分配 → 模拟外设 → ADC / DAC / 比较器**：

1. 勾选启用模拟 IP，按封装脚号勾选需要的 ADC 外部输入，勾选需要的 DAC 或比较器。
2. 按选项旁的封装脚号接线；引脚不是任意映射。例如 **AG32VF303CCT6 / LQFP48** 的 ADC_IN0 为 PIN10，DAC0 为 PIN14，DAC1 为 PIN15。VH303RCT6 的 DAC1 在 PIN_20，VH407VGT6 的 DAC1 在 PIN_30，两者均未引出 DAC0。
3. 点击 **保存并生成**。选中模拟引脚与数字映射冲突时会提示并禁止保存；不要把 ADC 通道添加为 GPIO 输出。
4. 顶部 **编译** 同时生成 MCU 固件和包含原厂 `analog_ip` 的逻辑镜像。实际使用时需下载两部分；仅更新 MCU 固件不能让未配置的模拟硬核工作。基础流程不需要 Quartus。

ADC_IN0–15 是外部通道编号，C API 使用 `ADC_CHANNEL0` 等原厂枚举，其数值不等于通道编号。内部温度通道 `ADC_CHANNEL16` 只支持 ADC1，不需要预留外部引脚。部分小封装没有引出全部外部通道，界面与构建都会拒绝不存在的通道。

示例要求先预留 ADC 通道 0、勾选 DAC0，再编译配套逻辑：

```c
#include "StudioX_System.h"

volatile uint16_t adc_value;
volatile StudioX_AnalogResult adc_status;

int main(void)
{
    /* DAC 为 10 位原始码；512 约为半量程，实际电压由参考与电路决定。 */
    (void)StudioX_DAC_Write(DAC0, 512);
    for (;;)
    {
        uint16_t sample;
        adc_status = StudioX_ADC_Read(ADC0, ADC_CHANNEL0, 1000, &sample);
        if (adc_status == STUDIOX_ANALOG_OK)
        {
            adc_value = sample; /* ADC 为 12 位原始码。 */
        }
        Delay_ms(10);
    }
}
```

`StudioX_ADC_Read` 按实际模拟逻辑总线频率计算 SCLK（不超过 10 MHz），清除旧完成标志，有界等待并返回参数错误、忙、超时或时钟不可用；失败保持调用者结果变量不变。显式 BUSCLK 使用独立 PLL 输出，否则跟随 SYS；不把 MCU APB 分频误当成逻辑时钟。`StudioX_AnalogClockHz()` 可查询这个频率；所需 PLL 未就绪时，便捷接口先退出，不访问未响应的模拟寄存器。不要在 ISR 或多个任务中并发使用同一 ADC，也不要在调用期间改变时钟；它不是 DMA 或 RTOS 互斥驱动。`StudioX_DAC_Write` 检查 0–1023 范围和已选通道，首次调用才使能缓冲输出，拒绝覆盖正在运行的 DMA。

在同页的 **模拟外设** 中选择具体实例。ADC0～2 支持最多 16 项的有序扫描、分频和启动方式，序列允许重复通道。DAC0/1 可设 10 位初值、缓冲和启动方式。CMP1/2 分别选择同相输入、反相输入或参考电压。点击“预留当前模拟输入 / 输出”可直接更新封装图。只启用旧式比较器总开关的工程继续预留原有输入。ADC2 与 DAC1 共用一条 DMA 请求线。

配置以受管注释保存在 `.ve` 中，例如：

```text
#@StudioX:ANALOG adc=0x0001 dac=1 cmp=0
```

它表示外部 ADC 通道 0、DAC0，关闭比较器预留。重复、非法或不匹配封装的配置会被拒绝。关闭模拟 IP 会删除此标记并释放引脚。

自动模拟配置目前用于基础 MCU / FreeRTOS 工程。已有自定义 Verilog 模式需要设计者自行组合模拟 IP、AHB 地址、DMA 请求及约束；IDE 不会把两个逻辑模块的总线直接并接。在自定义逻辑工程中仅粘贴上述注释会明确报错，避免生成不能访问模拟寄存器的固件。

## 时钟与时序防护

引脚映射页显示当前配置的时序状态、SYS/BUS 目标频率、最差建立/保持余量、覆盖连接数和关键路径，并可打开 Supra 原始报告。工具显示“0 错误、0 警告”不等于时序满足：存在负余量或未覆盖连接时，构建失败并删除可下载的映像及成功凭据，保留厂商原始报告供排错。

- 红色：时钟配置不合法，或实际时序失败。
- 黄色：尚未编译、存在未保存修改、结果过期，或已通过但最小余量小于 0.5 ns。0.5 ns 只是界面提醒阈值，不是芯片额定规格；低余量不会冒充失败，负余量也不会因提醒阈值而被放行。
- 绿色：当前工程、VE、生成约束、工具与实际报告相符，建立/保持余量满足要求，覆盖完整且映像校验通过。外部器件的采样约束和实际电路仍需另行验证。

修改图形草稿或 VE 后，旧绿色结果不再用于当前编辑内容。失败记录也绑定当次工程、源码、生成约束和工具散列；损坏或不匹配的记录不显示为通过。已有成功工程可通过原来的完整构建凭据读取结果，无需仅为显示状态而重编。

在 **时钟 / 时序** 标签页，BUSCLK 先检查是否为 SYSCLK 的整数分频。例如 160/100 MHz 会在保存前报错，160/80 MHz 可进入厂商转换验证。BUSCLK=0 保留沿用 SYSCLK 的语义，但仍需明确 HSECLK/SYSCLK，以生成一致的系统初始化。可以直接在图形页修复数值合法但分频错误的已有配置。

启用官方模拟 IP 且 HSECLK 为实际 8 MHz 时，提供 **160/80 MHz** 和 **100/50 MHz** 两组 SYS/BUS 离线推荐。点击“应用推荐时钟并保存”会保存整份图形草稿并同步生成 `StudioX_Board.h` 和系统代码；保留 HSE、引脚与模拟通道选择。保存后仍需编译当前方案，推荐本身不能作为下载依据。其它外部输入频率和自定义 Verilog 工程不会套用这些推荐。

2026-09-30 的原厂 `analog_ip` 基础映射回归使用 HSE 8 MHz、UART0 TX PIN_2、ADC 通道 0、DAC0/1 与比较器。每个组合均运行 GCC 和 Supra 实际布局布线：

| SYS / BUS | 7 款型号结果 | 最差建立余量 | 保持余量范围 |
| --- | --- | --- | --- |
| 100 / 50 MHz | 全部通过 | +4.351 ns | +0.599 ns |
| 160 / 80 MHz | 全部通过，其中 3 款触发低余量提醒 | +0.601 ns | +0.414～+0.599 ns |
| 200 / 100 MHz | 全部按预期拒绝下载 | −0.649 ns | +0.301～+0.444 ns |

型号为 AG32VF303KCU6/CCT6/VCT6、AG32VH303RCT6、AG32VF407RGT6/VGT6、AG32VH407VGT6。160/80 MHz 触发提醒的是 AG32VF303CCT6/VCT6 与 AG32VH303RCT6。这组数据只证明对应夹具、开发环境组件版本和约束的离线结果，不能推断所有引脚组合、IP 或电路在相同频率下都满足时序。

回归工具：`dotnet run --project tools/StudioX.Ag32TimingValidation -c Release -- <toolsets> <AGM包目录> <新的输出目录>`。结果保留 `matrix.json`、各工程的原始构建报告和 `result.json`。可用 `<toolsets> --recheck-evidence <矩阵输出目录> <新的输出目录>` 重新读取既有 21 组证据，并在副本中验证报告损坏、过期及成功凭据缺失的处理，无需再次布线。

## 数字外设

在 **引脚分配** 中选择 UART、SPI、I²C、CAN、GPTIMER、MAC、USB 等功能并保存后，对仅做引脚映射的外设，系统层在 `main()` 前开启对应时钟并设置选中的复用位。启用串口初始化时，这些步骤由串口模块一并执行。不会把整个 GPIO 组一起切走，也不会自动开始收发。

普通 MCU 和 FreeRTOS 工程可在左侧打开 **AG32 引脚与外设配置 → 引脚分配**，选择 UART 实例，也可点击封装图上已分配的串口引脚定位。封装图和参数同屏，窄窗口使用“引脚 / UART”下拉框选实例。勾选 **在 main 中初始化此串口**，选择 TX、RX 或其中之一，填写波特率。引脚与封装图共用同一份草稿。保存前可以展开初始化代码预览。保存后，工程树的 `src/UART.c` 包含时钟、复用、UART 参数和接收中断实现，`include/UART.h` 声明公共接口。生成器在 `main()` 开头加入 `SX_UART_Init();` 并登记 CMake 源文件。普通 MCU 与 FreeRTOS 共用这一入口。关闭所有串口初始化后，自动生成区会撤下 UART 头文件与调用。已生成的模块保留空入口，供手写代码继续引用。

`main.c` 的两个 `STUDIOX GENERATED BEGIN/END` 区随图形配置更新。`Includes` 放生成的外设头文件，`Init` 放初始化调用。自定义头文件放在 `USER CODE BEGIN/END Includes` 之间，需要在外设初始化前后执行的代码分别放在 `BeforeInit` 和 `AfterInit` 用户区。重新生成会保留用户区及区外原有业务代码。已有工程首次生成时会补上边界。删除生成的头文件引用、初始化调用、整个生成区或单边注释后，再次点击「保存并生成」即可按当前配置补回。单边注释旁的自定义语句会保留，已有的手写初始化调用也会保留在原位置。

串口默认 8N1。展开 **帧格式与收发方向** 可选 5–8 个数据位、无 / 奇 / 偶 / 固定 1 / 固定 0 校验和 1 或 2 个停止位。默认按映射脚启用收发，也可指定只发、只收或双向。展开 **流控、FIFO 与中断优先级** 可切换 1 / 16 字节 FIFO、选择 RTS / CTS 引脚和接收中断优先级（1–15）。RTS 需要接收端，CTS 需要发送端。启用接收中断需要 RX，系统为该串口建立 63 字节可用的接收队列。例如 UART0 可在主循环或一个任务中调用 `StudioX_UART0_TryRead(&value)`，其中 `value` 为 `uint8_t`，返回 true 时取到一字节。`StudioX_UART0_Dropped` 统计队列满时丢弃的新字节。有自定义强定义 `UART0_isr` 时由该处理函数接管接收。FreeRTOS 由调度器开启全局中断。启动如果回退到 HSI 且实际时钟无法产生设定波特率，系统会保留未启用状态，可用 `UART_IsEnabled(UART0)` 检查。

在 **中断 / PLIC** 勾选事件后，点击对应回调按钮可进入用户实现。首次点击会按当前生成的完整签名在工程入口的用户代码中添加函数。再次点击可定位到已有定义，未保存的编辑也参与查找。GPIO、定时器和通信外设共用此入口。

- **SPI DMA** 可为发送和接收分别选择通道 0～7。每条数据路径独占一个通道，所有 SPI DMA 通道共用 DMAC0_INTR 优先级。以 SPI1 为例，`SX_SPI1_SendDma` 发送 5～4092 字节，`SX_SPI1_ReceiveDma` 先发 1～4 字节命令，再接收 1～4092 字节。缓冲区以 32 位字分配，保留到完成或错误回调。
- **CAN 接收过滤** 支持标准和扩展 ID 的精确匹配或掩码匹配。界面掩码的 1 表示比较，0 表示忽略。例如标准 ID `0x123` 配掩码 `0x7F0`，会匹配 `0x120` 到 `0x12F`。接收中断也检查实际帧格式。使用 SDK 轮询接收时可调用 `SX_CAN0_MatchesFilter` 核对帧。
- **系统外设 → RTC** 使用 32.768 kHz 外部低速晶振，默认预分频装载值 32767，对应一秒计数一次。初始化保留已有时间，应用可调用 `SX_RTC_IsReady()` 检查结果。中断页可选择计数周期、闹钟比较和溢出事件。
- **系统外设 → WDOG** 按毫秒填写复位超时。启用复位前预警后，半程调用 `SX_WDOG_Warning()`。应用在确认关键任务运行正常后调用 `SX_WDOG_Feed()`。

I²C 上拉、CAN 收发器、USB 时钟和 MAC PHY 取决于实际电路。

SDK 的 `PERIPHERAL_ENABLE` 宏会同时处理一组默认复用位；已由图形配置选择部分引脚时，应优先保留生成的逐位初始化，避免再次启用整组默认引脚。

## 来源与旧工程

原厂输入来自已锁定的本机 `framework-agrv_sdk` / `framework-agrv_ips`。导入脚本 `tools/Import-Ag32Peripherals.py` 记录源文件 SHA-256、包版本、许可信息和官方引脚表；嵌入资源位于 `src/StudioX.Engine/Resources/Ag32/Peripherals/`，保留 C/H/VX/ASF/SDC 原始字节。固定引脚数据取自官方 `AG32_pinout_100_64_48_32_2K.xlsx`。

保存引脚配置或通过 IDE 配置编译时，兼容旧的精简包；驱动补到受管 `device/studiox/vendor/`，版本化时钟元数据生成到 `StudioX_Board.h`，同时更新器件 CMake。生成器只更新主函数入口中标明的外设头文件和初始化区域。原厂头文件版本不匹配、受管文件被外部编辑时停止并保留修改。新包配方也会保留完整原厂外设 C 源文件，受管系统层统一选择编译来源以避免重复定义。

依据：[AGM 外设使用说明](https://www.ag32mcu.com/dev-docs/doc_ag32_driver_use/)、[模拟 IP 说明](https://www.ag32mcu.com/dev-docs/doc_ag32_analog_code_analysis/)、[ADC 逻辑说明](https://www.ag32mcu.com/dev-docs/doc_ag32_analog_adc_modify/)。

## 验证范围

验证入口为 `tools/StudioX.Ag32PeripheralValidation`。GCC 从真实头文件提取 885 个原厂/模拟接口（包括内联函数），七个当前核实型号全部真实编译链接通过，C++ 也完成整套接口的链接。七款型号的原厂 IP 实际 Supra 布局布线均为 0 错误、0 警告，并验证映像凭据和硬核位置。

另有引脚冲突、无数字 IO 的纯模拟配置、过期映像和修改过的 SDK 拒绝检查。宿主寄存器模型执行生成的 C 代码：独立 BUSCLK 与 SYS 直连两种拓扑共 42 项检查，覆盖 ADC/DAC 边界、旧 EOC、超时、计数回绕、缺失 PLL、DMA 忙状态；这些是模型执行结果，不是实板波形。

此外运行现有系统层、普通/混合 FreeRTOS 和 WPF 操作回归。完整结果与工具原始日志保留于 `artifacts/validation/ag32-peripherals-20260930/`。按本轮用户选择，只做离线验收；没有连接、烧录硬件，也没有确认模拟精度、实际电压或外部总线通信。

## 在引脚分配中配置中断、DMA 和系统外设

| 配置对象 | 配置与生成接口 |
| --- | --- |
| EXT_INT0～7、LOCAL_INT0～3 | 外部中断可设置 PLIC 优先级。直连输入选择已映射的低有效外部信号，优先级由内核固定。在应用自己的 C 文件中实现对应的 `SX_EXT_INTn_Interrupt()` 或 `SX_LOCAL_INTn_Interrupt()`，并解除中断源的有效电平。 |
| DMA_CH0～7 | 选择 UART 收发、GPTIMER 更新/比较/捕获、ADC 或 DAC 请求。SPI 的通道在 SPI 实例中配置。通道号和硬件请求号分别校验，例如 SPI0_TX 和 UART4_RX 均使用请求 6。传输使用生成的 `Start(buffer, count)` 和 `Poll()`。保持缓冲区有效直到传输结束。 |
| GPTIMER0～4 | 在通道和时基下方设置单脉冲、外部 ETR、捕获输入或相邻定时器的 TRGO。先配置主触发输出，在从定时器选择触发源和启动、门控或复位模式。 |
| IWDG、PWR | 独立看门狗可选 LSI/LSE、分频及低功耗冻结，使用 `SX_IWDG_Reload()` 喂狗。PWR 可配置 Stop 外部唤醒边沿和 RTC 闹钟、Standby 的 WKUP/RTC。应用调用 SX_POWER_EnterStandby 时可选择保留现有闹钟，或按设定的 RTC 计数安排新的唤醒时间。 |
| USB0 | 选择全速设备或主机、内部 PLL 或外部 60 MHz 输入。设备 EP0 为 64 字节，EP1～3 各方向可选类型和包长。主机提供异步控制队列入口，板级 VBUS、电源控制和 USB 类协议由应用接入。 |
| MAC0 | 选择 MII/RMII、参考时钟方向、PHY 地址、单播 MAC 地址、速率和双工。初始化建立描述符并检查 PHY，应用通过 `SX_ETH_Handle()` 获取就绪句柄。板级 PHY 复位、电源和网络协议栈由应用接入。 |

保存会更新 `src/` 中的对应模块、`include/` 中的头文件以及 `main.c` 标注区中的初始化调用。USB 数据脚由封装决定；修改实际引脚和时钟后，编译并核对当前工程的 Supra 时序报告。

### 引脚分配中的高级设置

| 位置 | 使用方式 |
| --- | --- |
| SPI、I²C、CAN、UART | SPI 事务可组合最多八个阶段，设置线数、方向和轮询条件。I²C 可选 1 MHz。CAN 支持只听、自测、采样次数、SJW 与双组过滤。UART 可分别设置两个 FIFO 的水位及内部回环。 |
| GPTIMER 和基本定时器 | GPTIMER 可设中心对齐、重复次数、两路输出极性、ETR 和刹车滤波。基本定时器支持自由运行、周期或单次和 16 / 32 位计数。 |
| CRC0、BKP 和 RTC | CRC 种子与系数按所选位宽输入。BKP 的读写接口访问所选寄存器范围。RTC 校准与秒 / 闹钟输出使用封装的 IO_RTC 引脚。RTC 时钟源一经写入，切换需复位备份域。应用可用 SX_RTC_IsReady 检查，复位决策留给应用。 |
| 中断与 DMA | DMA 可分段连续搬运、复制内存，或由定时器换相、触发事件请求。完成和错误可选择回调。USB、MAC、Flash、FCB 的中断事件和优先级在“中断 / PLIC”设置。Flash 完成或错误中断需要在应用主动解锁后调用 SX_FLASH_ArmInterrupts。 |
| MAC0 与电源 | 自动协商时，周期调用 SX_ETH_RefreshLink 更新链路速率与双工方式。进入 Standby 可保留已有闹钟，也可按相对 RTC 计数设置新闹钟。 |

VH303RCT6 和 VH407VGT6 的 HyperRAM 页面显示封装容量、RWDS 板级连接和时钟条件。启用控制器需要在构建 IDE 前，将可使用的 `hram_ip.vx` 和配套 `hram_ip.sdc` 放入 `src/StudioX.Engine/Resources/Ag32/Peripherals/`。控制器资源与仿真所需的厂商参考 RTL 从本机准备，来源与要求见 [HyperRAM 验证说明](../tools/StudioX.Ag32PeripheralValidation/hyperram/README.md)。
