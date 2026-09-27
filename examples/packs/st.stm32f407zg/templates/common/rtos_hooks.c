#include "board.h"
#include "FreeRTOS.h"
#include "task.h"

/* 钩子可能在资源耗尽或栈损坏时执行；直接保留错误码，不再分配内存或调用日志任务。 */
void vApplicationMallocFailedHook(void)
{
    StudioX_Panic(30);
}

void vApplicationStackOverflowHook(TaskHandle_t task, char *name)
{
    (void)task;
    (void)name;
    StudioX_Panic(31);
}
