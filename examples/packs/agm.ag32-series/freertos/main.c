#include "StudioX_System.h"

volatile uint32_t app_heartbeat = 0;
volatile uint32_t app_worker_count = 0;

static void HeartbeatTask(void *argument)
{
    (void)argument;
    for (;;)
    {
        ++app_heartbeat;
        /* 任务等待让出 CPU；Delay_ms/us 是忙等待，适合短时外设时序。 */
        vTaskDelay(pdMS_TO_TICKS(500));
    }
}

static void WorkerTask(void *argument)
{
    (void)argument;
    for (;;)
    {
        ++app_worker_count;
        vTaskDelay(pdMS_TO_TICKS(100));
    }
}

int main(void)
{
    BaseType_t result = xTaskCreate(HeartbeatTask, "heartbeat", 256, NULL, 1, NULL);
    if (result != pdPASS) { StudioX_RtosAssert("heartbeat task", 0); }
    result = xTaskCreate(WorkerTask, "worker", 256, NULL, 1, NULL);
    if (result != pdPASS) { StudioX_RtosAssert("worker task", 0); }
    vTaskStartScheduler();

    /* 仅在调度器启动失败时返回。 */
    configASSERT(0);
    for (;;) { }
}
