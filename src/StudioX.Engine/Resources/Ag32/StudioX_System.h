/* StudioX generated AG32 system support v1. Edit the pin planner / VE, not this file. */
#ifndef STUDIOX_SYSTEM_H
#define STUDIOX_SYSTEM_H

/* 原厂统一入口包含全部外设；不复制寄存器声明或替换厂商驱动。 */
#include "alta.h"

@@CONFIG@@

#ifdef __cplusplus
extern "C" {
#endif

typedef struct
{
    GPIO_TypeDef *Port;
    uint8_t Bit;
    uint16_t Pin;
} StudioX_Pin;

typedef enum
{
    STUDIOX_SYSTEM_OK = 0,
    STUDIOX_SYSTEM_HSE_TIMEOUT = 1,
    STUDIOX_SYSTEM_PLL_TIMEOUT = 2
} StudioX_SystemResult;

/* 启动代码进入 main 前自动调用；晶振/PLL 超时退回 HSI，结果可在调试器查看。 */
extern volatile StudioX_SystemResult StudioX_SystemStatus;
void StudioX_SystemInit(void);
uint32_t StudioX_SystemClockHz(void);
uint32_t StudioX_PeripheralClockHz(void);

/* 阻塞延迟，单位分别为微秒/毫秒。0 不等待；不占用 mtime 或外设定时器。
 * 使用当前时钟及硬件周期计数，处理中断可能延长等待；等待期间不要改变时钟。
 * 请勿在 ISR 中作长时间等待。时钟精度仍取决于实际晶振 / HSI。 */
void Delay_us(uint32_t microseconds);
void Delay_ms(uint32_t milliseconds);
void Delay_1ms(void);

@@PINS@@

#ifdef __cplusplus
}
#endif
#ifdef STUDIOX_FREERTOS
/* RTOS 模板同样使用统一入口，配置保留在 include/FreeRTOSConfig.h。 */
#include "FreeRTOS.h"
#include "task.h"
#include "queue.h"
#include "semphr.h"
#include "timers.h"
#include "event_groups.h"
#include "stream_buffer.h"
#include "message_buffer.h"
#endif
#endif
