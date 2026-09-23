#include <stdint.h>
#include "py32f4xx.h"

volatile uint32_t app_counter;

int main(void)
{
    /* SystemInit 使用内部时钟；应用按实际电路配置引脚。 */
    for (;;) {
        ++app_counter;
        __NOP();
    }
}
