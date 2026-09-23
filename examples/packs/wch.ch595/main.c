#include "CH59x_common.h"

/* 可在调试器中观察；不假定开发板的 LED 或串口引脚接线。 */
volatile uint32_t studiox_heartbeat;

int main(void)
{
    /* 内部 HSI + PLL，避免空白工程依赖未知的板载外部晶振。 */
    SetSysClock(CLK_SOURCE_HSI_PLL_80MHz);

    while (1)
    {
        studiox_heartbeat++;
    }
}
