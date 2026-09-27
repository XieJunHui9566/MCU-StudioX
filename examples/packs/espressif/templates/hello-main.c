#include <stdio.h>

#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

void app_main(void)
{
    puts("MCU StudioX: native Espressif SDK application");

    // 通用目标没有预设 LED、晶振或外设接线；先用 SDK 控制台验证调度和运行状态。
    for (;;)
    {
        printf("Free heap: %lu bytes\n", (unsigned long)esp_get_free_heap_size());
        vTaskDelay(pdMS_TO_TICKS(1000));
    }
}
