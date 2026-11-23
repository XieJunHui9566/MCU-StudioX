#include "StudioX_System.h"
/* STUDIOX GENERATED BEGIN Includes */
/* 图形配置重新生成时会替换本区内容。请将自定义代码写在区外。 */
#include "GPIO.h"
/* STUDIOX GENERATED END Includes */

/* USER CODE BEGIN Includes */

/* USER CODE END Includes */

/* 将 LED1 配置为输出并保存后，GPIO 模块会包含相应引脚初始化。 */
int main(void)
{
    /* USER CODE BEGIN BeforeInit */

    /* USER CODE END BeforeInit */

    /* STUDIOX GENERATED BEGIN Init */
    /* 图形配置重新生成时会替换本区内容。请将自定义代码写在区外。 */
    SX_GPIO_Init();
    /* STUDIOX GENERATED END Init */

    /* USER CODE BEGIN AfterInit */

    /* USER CODE END AfterInit */
    while (1)
    {
        GPIO_SetHigh(LED1_Port, LED1_Bit);
        Delay_ms(500);

        GPIO_SetLow(LED1_Port, LED1_Bit);
        Delay_ms(500);
    }
}
