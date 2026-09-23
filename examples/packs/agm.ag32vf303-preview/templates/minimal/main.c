/*
 * MCU StudioX / AG32VF303CCT6
 * 临时模板：用于测试新建工程与源文件编辑。
 * 不假设开发板 LED、串口、晶振或逻辑区域的配置。
 */
#include <stdint.h>
#include "system.h"
#include "interrupt.h"

volatile uint32_t app_heartbeat = 0;
volatile uint32_t app_device_id = 0;

int main(void)
{
    INT_DisableIntGlobal();

    /* 使用内部时钟，不依赖板载外部晶振。 */
    SYS->CLK_CNTL &= ~SYS_CLK_SOURCE_MASK;
    SYS->CLK_CNTL &= ~(SYS_CLK_PLL_ON | SYS_CLK_HSE_ON);
    app_device_id = SYS_GetDeviceID();

    for (;;) {
        ++app_heartbeat;
        /* TODO: 在这里编写你的应用。 */
    }
}
