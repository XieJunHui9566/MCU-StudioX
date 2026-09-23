#include <stdint.h>
#include "py32f4xx_hal.h"

volatile uint32_t app_counter;

int main(void)
{
    HAL_Init();
    /* 不预设外部晶振或任何开发板引脚。 */
    for (;;) {
        ++app_counter;
        __NOP();
    }
}
