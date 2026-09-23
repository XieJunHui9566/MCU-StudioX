/* STM32F407ZGT6 · HSE 8 MHz → 168 MHz。
 * 在这里添加应用代码；不预设 LED、UART 等板级引脚。
 * 外部晶振异常会停在 StudioX_Panic，app_error_code 可用于定位。
 */
#include "board.h"
#ifdef STUDIOX_USE_FREERTOS
#include "FreeRTOS.h"
#include "task.h"
#endif

volatile uint32_t app_heartbeat;
volatile uint32_t app_sysclk_hz;

#ifdef STUDIOX_USE_FREERTOS
static void HeartbeatTask(void *argument)
{
    (void)argument;
    TickType_t wake = xTaskGetTickCount();
    for (;;) {
        ++app_heartbeat;
        vTaskDelayUntil(&wake, pdMS_TO_TICKS(500));
    }
}
#endif

int main(void)
{
#ifdef USE_HAL_DRIVER
    if (HAL_Init() != HAL_OK) StudioX_Panic(20);
#endif
    BoardClock_Init();
    app_sysclk_hz = SystemCoreClock;
#ifdef STUDIOX_USE_FREERTOS
    if (xTaskCreate(HeartbeatTask, "heartbeat", 256, NULL, 2, NULL) != pdPASS) StudioX_Panic(21);
    vTaskStartScheduler();
    StudioX_Panic(22);
#else
    for (;;) {
        ++app_heartbeat;
        Board_Delay(500);
    }
#endif
}
