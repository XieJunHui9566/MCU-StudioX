/*
 * MCU StudioX / AG32 裸机入口
 * 片内时钟和内存计数不依赖开发板接线；外设使用前先在 logic/pins.ve 中绑定引脚。
 * VH 系列的 PSRAM 需要对应 HyperBus 逻辑，本入口不把它当作已初始化的片内 SRAM。
 */
#include <stdint.h>
#include "system.h"
#include "interrupt.h"

volatile uint32_t app_heartbeat = 0;
volatile uint32_t app_device_id = 0;

int main(void)
{
    INT_DisableIntGlobal();

    /* 使用内部时钟；SYSCLK 最大值不代表当前工程已运行在该频率。 */
    SYS->CLK_CNTL &= ~SYS_CLK_SOURCE_MASK;
    SYS->CLK_CNTL &= ~(SYS_CLK_PLL_ON | SYS_CLK_HSE_ON);
    app_device_id = SYS_GetDeviceID();

    for (;;)
    {
        ++app_heartbeat;
    }
}
