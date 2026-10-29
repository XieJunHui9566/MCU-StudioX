#include "../include/AI8051U.h"

volatile __xdata uint32_t app_heartbeat = 0;
volatile __xdata uint8_t app_input = 0;

static void gpio_init(void)
{
    /* P2.0仅作高阻输入；不假定底板LED接线，不主动驱动外部电路。
     * M1/M0=10为高阻输入，其余端口模式位保持原值。 */
    P2M1 |= 0x01U;
    P2M0 &= 0xFEU;
}

void main(void)
{
    /* 配置好中断源及服务程序之前保持总中断关闭。 */
    EA = 0;
    gpio_init();
    for (;;)
    {
        app_input = P2 & 0x01U;
        ++app_heartbeat;
    }
}
