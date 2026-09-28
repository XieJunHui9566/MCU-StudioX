#include "StudioX_System.h"

volatile uint32_t app_heartbeat = 0;
volatile uint32_t app_logic_value = 0;

static void HeartbeatTask(void *argument)
{
    (void)argument;
    for (;;)
    {
        ++app_heartbeat;
        vTaskDelay(pdMS_TO_TICKS(500));
    }
}

static void LogicTask(void *argument)
{
    (void)argument;
    for (;;)
    {
        /* 内部 GPIO4_0 经 user_logic.v 回到 GPIO4_1，不连接封装脚。 */
        GPIO_Toggle(GPIO4, GPIO_BIT0);
        vTaskDelay(pdMS_TO_TICKS(100));
        app_logic_value = GPIO_GetValue(GPIO4, GPIO_BIT1);
    }
}

int main(void)
{
    BaseType_t result = xTaskCreate(HeartbeatTask, "heartbeat", 256, NULL, 1, NULL);
    if (result != pdPASS) { StudioX_RtosAssert("heartbeat task", 0); }
    result = xTaskCreate(LogicTask, "logic", 256, NULL, 1, NULL);
    if (result != pdPASS) { StudioX_RtosAssert("logic task", 0); }
    vTaskStartScheduler();
    configASSERT(0);
    for (;;) { }
}
