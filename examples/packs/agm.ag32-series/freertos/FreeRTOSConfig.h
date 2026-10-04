#ifndef FREERTOS_CONFIG_H
#define FREERTOS_CONFIG_H

#include "alta.h"

/* 使用 AGM 的 CLINT 定义；实际计数频率在调度开始前由系统层读取。 */
#define configMTIME_BASE_ADDRESS (CLINT_BASE + 0xBFF8U)
#define configMTIMECMP_BASE_ADDRESS (CLINT_BASE + 0x4000U)
#define configCPU_CLOCK_HZ (SYS_GetSysClkFreq())
#define configTICK_RATE_HZ 1000
#define configISR_STACK_SIZE_WORDS 512
#define configUSE_PREEMPTION 1
#define configUSE_TIME_SLICING 1
#define configUSE_PORT_OPTIMISED_TASK_SELECTION 1
#define configMAX_PRIORITIES 8
#define configMINIMAL_STACK_SIZE 256
#define configMAX_TASK_NAME_LEN 16
#define configUSE_16_BIT_TICKS 0
#define configQUEUE_REGISTRY_SIZE 8

/* heap_4 仅占片内 SRAM；VH 的 PSRAM 不作为默认 RTOS 堆。栈深度单位为 32 位字。 */
#define configSUPPORT_STATIC_ALLOCATION 1
#define configSUPPORT_DYNAMIC_ALLOCATION 1
#define configTOTAL_HEAP_SIZE (32 * 1024)
#define configUSE_IDLE_HOOK 0
#define configUSE_TICK_HOOK 0
#define configCHECK_FOR_STACK_OVERFLOW 2
#define configUSE_MALLOC_FAILED_HOOK 1
#define configGENERATE_RUN_TIME_STATS 0
#define configUSE_TRACE_FACILITY 1
#define configUSE_STATS_FORMATTING_FUNCTIONS 0
#define configUSE_TIMERS 1
#define configTIMER_TASK_PRIORITY 2
#define configTIMER_QUEUE_LENGTH 8
#define configTIMER_TASK_STACK_DEPTH 256

#ifdef __cplusplus
extern "C"
{
#endif
    void StudioX_RtosAssert(const char *file, uint32_t line);
#ifdef __cplusplus
}
#endif
#define configASSERT(condition)                                                                    \
    do                                                                                             \
    {                                                                                              \
        if (!(condition))                                                                          \
            StudioX_RtosAssert(__FILE__, __LINE__);                                                \
    } while (0)

#define INCLUDE_vTaskPrioritySet 1
#define INCLUDE_uxTaskPriorityGet 1
#define INCLUDE_vTaskDelete 1
#define INCLUDE_vTaskSuspend 1
#define INCLUDE_xTaskResumeFromISR 1
#define INCLUDE_xTaskDelayUntil 1
#define INCLUDE_vTaskDelay 1
#define INCLUDE_xTaskGetSchedulerState 1
#define INCLUDE_xTaskGetCurrentTaskHandle 1
#define INCLUDE_uxTaskGetStackHighWaterMark 1
#define INCLUDE_uxTaskGetStackHighWaterMark2 1
#define INCLUDE_xTaskGetIdleTaskHandle 1
#define INCLUDE_eTaskGetState 1
#define INCLUDE_xTimerPendFunctionCall 1
#define INCLUDE_xTaskAbortDelay 1
#define INCLUDE_xTaskGetHandle 1

#endif
