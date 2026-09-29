#pragma once
#include "StudioX_Device.h"
#include <stdint.h>

/* 少数原厂寄存器头没有包含 system 头；显式声明 CMSIS 标准系统接口。 */
#ifndef STUDIOX_CLOCK_DECLARED_BY_DEVICE
extern uint32_t SystemCoreClock;
#endif
#ifndef STUDIOX_CLOCK_FROM_SYSTEM_INIT
void SystemCoreClockUpdate(void);
#endif

/* 使用原厂 SystemInit 的复位时钟配置；改变时钟后需重新配置系统时基。 */
void StudioX_System_Init(void);
uint32_t StudioX_Millis(void);
/* 仅在线程上下文且中断开启时调用；中断中使用非阻塞状态机。 */
void StudioX_DelayMs(uint32_t milliseconds);
