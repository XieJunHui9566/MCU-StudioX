#include <stdio.h>

#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static void heartbeat_task(void *argument)
{
    (void)argument;

    for (;;)
    {
        printf("Heartbeat; free heap: %lu bytes\n", (unsigned long)esp_get_free_heap_size());
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}

void app_main(void)
{
    // RTOS SDK v3.4 的 ESP8266 port 使用 uint8_t StackType_t；这里为打印任务保留 2048 字节栈。
    const BaseType_t created = xTaskCreate(heartbeat_task, "heartbeat", 2048, NULL, 1, NULL);
    if (created != pdPASS)
    {
        puts("Failed to create heartbeat task: insufficient task resources");
    }
}
