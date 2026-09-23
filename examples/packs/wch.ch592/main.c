#include "CH59x_common.h"

/* 调试观察项；空白工程不假定板载 LED 或 UART 引脚。 */
volatile uint32_t studiox_heartbeat;

int main(void)
{
    /* 沿用 WCH CH592 官方 NoneOS 模板的 60 MHz 时钟配置。 */
    SetSysClock(CLK_SOURCE_PLL_60MHz);

    while (1)
    {
        studiox_heartbeat++;
    }
}
