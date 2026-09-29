#include "StudioX_System.h"

int main(void)
{
    StudioX_System_Init();

    for (;;)
    {
        /* 在这里添加应用；默认不占用 LED、串口或其他板级引脚。 */
        __WFI();
    }
}
