#ifndef STUDIOX_SYSTEM_CONFIG_H
#define STUDIOX_SYSTEM_CONFIG_H

#include "board.h"
#ifdef STUDIOX_USE_FREERTOS
#include "FreeRTOS.h"
#include "task.h"
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* 初始化库、外部晶振、系统时钟和系统时基；具体配置见 device/system。 */
void System_Init(void);

/* 毫秒延时；RTOS 工程中只能在调度器启动后的任务内调用。 */
void System_Delay(uint32_t milliseconds);

#ifdef STUDIOX_USE_FREERTOS
/* 先创建应用任务，再启动调度器。正常情况下不返回。 */
void System_Start(void) __attribute__((noreturn));
#endif

#ifdef __cplusplus
}
#endif
#endif
