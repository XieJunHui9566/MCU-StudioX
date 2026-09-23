#include "board.h"
#include "FreeRTOS.h"
#include "task.h"

void vApplicationMallocFailedHook(void) { StudioX_Panic(30); }
void vApplicationStackOverflowHook(TaskHandle_t task, char *name)
{
    (void)task; (void)name;
    StudioX_Panic(31);
}
