#include "StudioX_System.h"

/* 在引脚分配界面将 LED1 配置为输出，系统层会在 main 前完成初始化。 */
int main(void)
{
    for (;;)
    {
        GPIO_SetHigh(LED1_Port, LED1_Bit);
        Delay_ms(500);

        GPIO_SetLow(LED1_Port, LED1_Bit);
        Delay_ms(500);
    }
}
