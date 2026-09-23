#include <stdint.h>
#include "py32f4xx.h"

volatile uint32_t app_counter;

int main(void)
{
    /* SystemInit 使用芯片内部 HSI；引脚和外设由应用按电路配置。 */
    for (;;) {
        ++app_counter;
        __NOP();
    }
}
