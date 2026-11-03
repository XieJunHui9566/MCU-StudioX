#include "py32f0xx.h"
#include "@HAL_HEADER@"

int main(void)
{
    HAL_Init();
    volatile uint32_t counter = 0;
    while (1)
    {
        ++counter; /* 无板级引脚假设；在此编写应用。 */
    }
}
