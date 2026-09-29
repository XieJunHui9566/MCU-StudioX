#include "StudioX_System.h"

static volatile uint32_t studiox_ticks;

void StudioX_System_Init(void)
{
#ifndef STUDIOX_CLOCK_FROM_SYSTEM_INIT
    SystemCoreClockUpdate();
#endif
    /* SysTick 属于当前裸机模板；接入 RTOS 时交由内核管理。 */
    if (SystemCoreClock < 1000u || SysTick_Config(SystemCoreClock / 1000u) != 0u)
    {
        for (;;) { __NOP(); }
    }
}

uint32_t StudioX_Millis(void) { return studiox_ticks; }

void StudioX_DelayMs(uint32_t milliseconds)
{
    const uint32_t start = studiox_ticks;
    while ((uint32_t)(studiox_ticks - start) < milliseconds) { __NOP(); }
}

void SysTick_Handler(void) { ++studiox_ticks; }
void _init(void) { }
void _fini(void) { }
