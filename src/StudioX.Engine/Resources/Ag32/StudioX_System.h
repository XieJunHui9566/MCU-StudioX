/* StudioX generated AG32 system support v1. Edit the pin planner / VE, not this file. */
#ifndef STUDIOX_SYSTEM_H
#define STUDIOX_SYSTEM_H

/* 原厂统一入口包含全部外设；不复制寄存器声明或替换厂商驱动。 */
#include "StudioX_Board.h"
#include "alta.h"

@@CONFIG@@

#if STUDIOX_ANALOG_ENABLED
#include "vendor/analog_ip.h"
#endif

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

#if STUDIOX_ANALOG_ENABLED
typedef enum
{
    STUDIOX_ANALOG_OK = 0,
    STUDIOX_ANALOG_INVALID_ARGUMENT = 1,
    STUDIOX_ANALOG_BUSY = 2,
    STUDIOX_ANALOG_TIMEOUT = 3,
    STUDIOX_ANALOG_CLOCK_UNAVAILABLE = 4
} StudioX_AnalogResult;

/* 模拟 IP 的逻辑总线时钟；与 MCU APB 分频不同。所需 PLL 未就绪时返回 0。 */
uint32_t StudioX_AnalogClockHz(void);

/* 单次阻塞采样，timeout_us 必须大于 0；禁止与 ISR / DMA / 其它任务并发操作同一 ADC。
 * channel 使用原厂 ADC_CHANNELx；外部通道须先在配置页预留引脚。
 * 内部温度 ADC_CHANNEL16 仅 ADC1 支持。调用期间不要改变时钟。
 * 底层原厂 ADC/DAC/CMP API 同时可用，但须先保证模拟 IP 和时钟已正确配置。 */
StudioX_AnalogResult StudioX_ADC_Read(ADC_TypeDef *adc, ADC_ChannelNumTypeDef channel,
    uint32_t timeout_us, uint16_t *value);
/* 10 位原始码 0..1023；对应 DAC 输出须在配置页勾选，首次调用才使能输出。 */
StudioX_AnalogResult StudioX_DAC_Write(DAC_TypeDef *dac, uint16_t value);
#endif

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
