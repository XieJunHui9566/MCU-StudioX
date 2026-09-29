# AG32 外设使用与离线验收

统一入口为 `#include "StudioX_System.h"`。IDE 会接入完整原厂 SDK 驱动实现，包含 GPIO、UART、SPI、I²C、CAN、DMA、通用/基础定时器、RTC、USB、MAC、CRC、FCB、Flash、系统时钟及看门狗；不再只有头文件声明。普通 MCU 和 FreeRTOS 模板使用同一系统层，应用代码留在 `src/`。

## ADC / DAC / CMP

普通 MCU / FreeRTOS 工程打开左侧 **AG32 引脚分配 → ADC / DAC / 比较器**：

1. 勾选启用模拟 IP，填写需要的 ADC 外部通道号，勾选需要的 DAC 或比较器。
2. 按页面中的当前封装固定引脚表接线；引脚不是任意映射。例如 **AG32VF303CCT6 / LQFP48** 的 ADC_IN0 为 PIN10，DAC0 为 PIN14，DAC1 为 PIN15。其它型号以其自己的表为准。
3. 点击 **保存并生成约束**。选中模拟引脚与数字映射冲突时会提示并禁止保存；不要把 ADC 通道添加为 GPIO 输出。
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

需要连续采样、DMA、比较器或无缓冲输出时，仍可使用原厂 `ADC_*`、`DAC_*`、`CMP_*` 和 `DMAC_*` API。原厂底层 API 保留原有语义，包括部分无超时等待；使用者负责初始化、时序和并发。ADC2 与 DAC1 共用 EXT_DMA2_REQ，不能同时配置这两个 DMA 请求。选择 CMP 会预留当前封装的所有比较器输入，具体正负输入由程序设置。

配置以受管注释保存在 `.ve` 中，例如：

```text
#@StudioX:ANALOG adc=0x0001 dac=1 cmp=0
```

它表示外部 ADC 通道 0、DAC0，关闭比较器预留。重复、非法或不匹配封装的配置会被拒绝。关闭模拟 IP 会删除此标记并释放引脚。

自动模拟配置目前用于基础 MCU / FreeRTOS 工程。已有自定义 Verilog 模式需要设计者自行组合模拟 IP、AHB 地址、DMA 请求及约束；IDE 不会把两个逻辑模块的总线直接并接。在自定义逻辑工程中仅粘贴上述注释会明确报错，避免生成不能访问模拟寄存器的固件。

## 数字外设

在 **引脚分配** 中选择 UART、SPI、I²C、CAN、GPTIMER、MAC、USB 等功能并保存后，系统层在 `main()` 前开启已分配模块的对应时钟，把实际选中的复用位切到硬件模式。不会把整个 GPIO 组一起切走，也不会自动开始收发。

程序仍需按 SDK 设置波特率、收发模式、定时周期、DMA 通道或中断。内部使用且未映射到引脚的外设须显式开启时钟（例如 `SYS_EnableAHBClock(AHB_MASK_DMAC0)`）。不要把开启时钟等同于外设已经初始化。I²C 上拉、CAN 收发器、USB 时钟/协议栈、MAC PHY 等依赖实际电路，编译通过不能替代这些条件。

SDK 的 `PERIPHERAL_ENABLE` 宏会同时处理一组默认复用位；已由图形配置选择部分引脚时，应优先保留生成的逐位初始化，避免再次启用整组默认引脚。

## 来源与旧工程

原厂输入来自已锁定的本机 `framework-agrv_sdk` / `framework-agrv_ips`。导入脚本 `tools/Import-Ag32Peripherals.py` 记录源文件 SHA-256、包版本、许可信息和官方引脚表；嵌入资源位于 `src/StudioX.Engine/Resources/Ag32/Peripherals/`，保留 C/H/VX/ASF/SDC 原始字节。固定引脚数据取自官方 `AG32_pinout_100_64_48_32_2K.xlsx`。

保存引脚配置或通过 IDE 配置编译时，兼容旧的精简包；驱动补到受管 `device/studiox/vendor/`，版本化时钟元数据生成到 `StudioX_Board.h`，同时更新器件 CMake。不会修改用户 `main.c` 或覆盖 `device/sdk`。原厂头文件版本不匹配、受管文件被外部编辑时停止并保留修改。新包配方也会保留完整原厂外设 C 源文件，受管系统层统一选择编译来源以避免重复定义。

依据：[AGM 外设使用说明](https://www.ag32mcu.com/dev-docs/doc_ag32_driver_use/)、[模拟 IP 说明](https://www.ag32mcu.com/dev-docs/doc_ag32_analog_code_analysis/)、[ADC 逻辑说明](https://www.ag32mcu.com/dev-docs/doc_ag32_analog_adc_modify/)。

## 验证范围

验证入口为 `tools/StudioX.Ag32PeripheralValidation`。GCC 从真实头文件提取 885 个原厂/模拟接口（包括内联函数），七个当前核实型号全部真实编译链接通过，C++ 也完成整套接口的链接。七款型号的原厂 IP 实际 Supra 布局布线均为 0 错误、0 警告，并验证映像凭据和硬核位置。

另有引脚冲突、无数字 IO 的纯模拟配置、过期映像和修改过的 SDK 拒绝检查。宿主寄存器模型执行生成的 C 代码：独立 BUSCLK 与 SYS 直连两种拓扑共 42 项检查，覆盖 ADC/DAC 边界、旧 EOC、超时、计数回绕、缺失 PLL、DMA 忙状态；这些是模型执行结果，不是实板波形。

此外运行现有系统层、普通/混合 FreeRTOS 和 WPF 操作回归。完整结果与工具原始日志保留于 `artifacts/validation/ag32-peripherals-20260930/`。按本轮用户选择，只做离线验收；没有连接、烧录硬件，也没有确认模拟精度、实际电压或外部总线通信。
