# CH32V307 中断约定与检查

2026-09-22 核对 `wch.ch32v307/0.1.1` 和 `wch.riscv/1.0.0`。中断入口的函数属性与普通函数不同；优化等级不能替代中断属性。

随后已完成 VCT6 + WCH-LinkE 的 RAM 实板检查：六档优化、单层/三级 fast、软件压栈及四级混合嵌套，见 中断实板验收（本地记录）。

## 当前模板

- 使用 WCH GCC 12.2.0 v1.4，`-march=rv32imac_xw -mabi=ilp32`，没有切换为通用 RISC-V 编译器。
- `device/sdk/Startup/startup_ch32v30x_D8C.S` 将 `INTSYSCR`（CSR `0x804`）设为 `0x0b`：硬件压栈、嵌套开启，四级优先级深度，硬件栈溢出后不继续允许中断。
- `device/system/system_config.c` 使用与之对应的 `NVIC_PriorityGroup_2`。
- `device/system/ch32v30x_it.c` 中的 NMI 和 HardFault 已有 `interrupt("WCH-Interrupt-fast")` 声明。其他向量在启动文件中是弱默认入口，未提供会返回的外设中断实现。

上述组合与 [WCH 官方嵌套中断示例的纯硬件压栈说明](https://github.com/openwch/ch32v307/blob/main/EVT/EXAM/INT/Interrupt_Nest/User/main.c)一致，本次没有修改启动寄存器或现有工程的中断行为。

## 应用中断写法

沿用当前默认启动方式时，用户新增的硬件中断应在**定义所在的编译单元**中使用带属性的声明，再实现处理函数，例如：

```c
#include "system_config.h"

void TIM2_IRQHandler(void) __attribute__((interrupt("WCH-Interrupt-fast")));

void TIM2_IRQHandler(void)
{
    if (TIM_GetITStatus(TIM2, TIM_IT_Update) != RESET)
    {
        TIM_ClearITPendingBit(TIM2, TIM_IT_Update);
        /* 在这里处理事件；外设时钟、计时器和中断使能由初始化代码配置。 */
    }
}
```

将新文件加入根 CMake 的源码列表。属性只写在另一个 `.c` 文件里不能影响这里的定义；函数名必须匹配启动向量。C++ 中应将声明和定义放进 `extern "C" { ... }`，避免名称改编。共享给主循环的状态按实际并发需求处理，简单事件标志通常需要 `volatile`，它不提供复合操作的原子性。不要从主程序直接调用 ISR，需要复用的逻辑提取为普通函数。

按照 [青稞 V4 手册第 2.4、3.4 节](https://www.wch.cn/uploads/file/20220411/1649641123177990.pdf)，中断返回使用 `mret`；硬件压栈中断依赖厂商开发环境组件和 fast 属性，软件压栈中断使用 `__attribute__((interrupt()))`。缺少属性的普通 C 函数可以编译成功，但编译器不会自动把它变成中断入口。

## 深层嵌套与 RTOS

CH32V307 有三级硬件栈，四级或八级优先级配置不等于四层或八层硬件栈。若打开硬件栈溢出后继续响应中断，需要配合高优先级的软件压栈入口。官方八级示例使用 `0x1f`，低三级用 fast、高五级用普通 interrupt 属性；不能把所有入口一律设为 fast。禁用硬件压栈时也应配套调整入口属性及优先级配置。参见 [官方嵌套示例](https://github.com/openwch/ch32v307/blob/main/EVT/EXAM/INT/Interrupt_Nest/User/main.c)。

涉及 RTOS 上下文切换时需遵循对应移植层的保存/恢复约定，不能直接照搬裸机 ISR。当前包仍是裸机 SPL 模板，本次不增加 RTOS 或切换硬浮点 ABI。

## 离线回归

```powershell
dotnet run --project tools/StudioX.WchValidation -c Release -- --interrupts artifacts/tool-runtime artifacts/ch32-interrupt-check-new
```

使用真实 WCH GCC、G++、objdump，覆盖 `-O0/-Og/-O1/-O2/-O3/-Os`，在含外部函数调用的 C/C++ 入口中核对 fast/software ISR 的 `mret`、普通函数无 `mret`、C++ 中断符号没有名称改编。采用 `-Wall -Wextra -Werror` 防止属性被忽略；记录目标文件和反汇编，不复制 SDK、不生成可下载固件。

`--interrupts` 只验证编译器生成的入口/返回约定，不连接硬件。独立的 `--prepare-interrupts-ram` / `--run-interrupts-ram` 及其实板范围见上方验收记录；它们不替代真实外设清标志时序检查。
