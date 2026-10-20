#include <xc.h>

/* 未假设板级晶振和引脚；配置位需要在确认实际板卡后另行设置。 */
volatile unsigned char app_heartbeat;

void main(void)
{
    for (;;)
    {
        CLRWDT();
        ++app_heartbeat;
    }
}
