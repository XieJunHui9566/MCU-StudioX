#include "board.h"

void NMI_Handler(void) { StudioX_Panic(10); }
void HardFault_Handler(void) { StudioX_Panic(11); }
void MemManage_Handler(void) { StudioX_Panic(12); }
void BusFault_Handler(void) { StudioX_Panic(13); }
void UsageFault_Handler(void) { StudioX_Panic(14); }
void DebugMon_Handler(void) { }

/* RTOS 工程由 Cortex-M4F port 提供 SVC/PendSV/SysTick，避免重复定义。 */
#ifndef STUDIOX_USE_FREERTOS
static volatile uint32_t board_ticks;
void SVC_Handler(void) { }
void PendSV_Handler(void) { }
void SysTick_Handler(void)
{
#ifdef USE_HAL_DRIVER
    HAL_IncTick();
#else
    ++board_ticks;
#endif
}
void Board_Delay(uint32_t milliseconds)
{
#ifdef USE_HAL_DRIVER
    HAL_Delay(milliseconds);
#else
    const uint32_t start = board_ticks;
    while ((uint32_t)(board_ticks - start) < milliseconds) { __WFI(); }
#endif
}
#endif
