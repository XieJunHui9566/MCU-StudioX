#include "system_config.h"

void System_Init(void)
{
    /* 官方启动代码在 main 前调用 SystemInit，时钟选项见本目录 system_ch32v30x.c。 */
    NVIC_PriorityGroupConfig(NVIC_PriorityGroup_2);
    SystemCoreClockUpdate();
    Delay_Init();

    /* 默认要求外部 8 MHz 晶振；启动失败时停止，避免以错误时钟操作外设。 */
    if ((RCC->CTLR & RCC_HSERDY) == 0)
        while (1) { }
}
