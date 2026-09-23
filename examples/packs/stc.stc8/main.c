#include "stc15.h"

/* 工程明确填写 RC 或外部晶振频率时，IDE 定义 STUDIOX_CLOCK_HZ（单位 Hz）。
 * UART 波特率、定时器重装值等应由此宏计算；未填写频率时不会定义。
 */
/* 无板级引脚假设：用片内 XRAM 中的计数值验证最小工程可以编译。 */
volatile __xdata unsigned int app_counter;

void main(void)
{
    for (;;)
    {
        ++app_counter;
    }
}
