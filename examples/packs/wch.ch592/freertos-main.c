#include "CH59x_common.h"
#include "FreeRTOS.h"
#include "task.h"

volatile uint32_t studiox_task_200ms_count;
volatile uint32_t studiox_task_500ms_count;
volatile uint32_t studiox_rtos_start_error;

static void Task200ms(void *argument)
{
    (void)argument;
    for (;;)
    {
        ++studiox_task_200ms_count;
        vTaskDelay(pdMS_TO_TICKS(200));
    }
}

static void Task500ms(void *argument)
{
    (void)argument;
    for (;;)
    {
        ++studiox_task_500ms_count;
        vTaskDelay(pdMS_TO_TICKS(500));
    }
}

int main(void)
{
    /* 使用官方 PLL 配置；不预设板级 GPIO 或开启 BLE/TMOS。 */
    SetSysClock(CLK_SOURCE_PLL_60MHz);
    if (xTaskCreate(Task200ms, "task200", 128, NULL, 2, NULL) != pdPASS ||
        xTaskCreate(Task500ms, "task500", 128, NULL, 2, NULL) != pdPASS)
    {
        studiox_rtos_start_error = 1;
    }
    else
    {
        vTaskStartScheduler();
        studiox_rtos_start_error = 2;
    }
    taskDISABLE_INTERRUPTS();
    for (;;) { }
}
