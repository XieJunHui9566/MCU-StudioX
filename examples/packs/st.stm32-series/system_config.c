#include "system_config.h"

/* 系统配置集中于 device/system；应用代码保留在 src/main.c。 */
void System_Init(void)
{
#ifdef USE_HAL_DRIVER
    if (HAL_Init() != HAL_OK) StudioX_Panic(20);
#endif
    BoardClock_Init();
}

void System_Delay(uint32_t milliseconds)
{
#ifdef STUDIOX_USE_FREERTOS
    vTaskDelay(pdMS_TO_TICKS(milliseconds));
#else
    Board_Delay(milliseconds);
#endif
}

#ifdef STUDIOX_USE_FREERTOS
void System_Start(void)
{
    vTaskStartScheduler();
    /* 调度器返回表示空闲/定时器任务创建失败，保留可调试错误码。 */
    StudioX_Panic(22);
    for (;;) { }
}
#endif
