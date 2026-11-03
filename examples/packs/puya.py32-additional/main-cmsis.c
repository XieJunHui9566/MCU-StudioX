#include "py32f0xx.h"

int main(void)
{
    SystemCoreClockUpdate();
    volatile uint32_t counter = 0;
    while (1)
    {
        ++counter; /* 无板级引脚假设；在此编写应用。 */
    }
}
