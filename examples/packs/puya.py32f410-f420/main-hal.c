#include <stdint.h>
#include "py32f4xx_hal.h"

volatile uint32_t app_counter;

int main(void)
{
    HAL_Init();
    /* 不假设外部晶振、开发板 LED 或调试探针。 */
    for (;;) {
        ++app_counter;
        __NOP();
    }
}
