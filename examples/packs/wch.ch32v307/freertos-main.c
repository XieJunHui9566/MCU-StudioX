#include "system_config.h"
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
    System_Init();
    /* 仅使用可观察计数器，不预设开发板 LED 或串口引脚。 */
    if (xTaskCreate(Task200ms, "task200", 128, NULL, 2, NULL) != pdPASS ||
        xTaskCreate(Task500ms, "task500", 128, NULL, 2, NULL) != pdPASS)
    {
        studiox_rtos_start_error = 1;
    }
    else
    {
        vTaskStartScheduler();
        /* 调度器返回意味着空闲任务或堆分配失败，保留可调试状态。 */
        studiox_rtos_start_error = 2;
    }
    taskDISABLE_INTERRUPTS();
    for (;;) { }
}
