#include "board.h"

volatile uint32_t app_error_code;

void StudioX_Panic(uint32_t code)
{
    app_error_code = code;
    __disable_irq();
    for (;;) { __NOP(); }
}

/* HSE 8 MHz / M=8 * N=336 / P=2 = 168 MHz，Q=7 提供 48 MHz。 */
void BoardClock_Init(void)
{
#ifdef USE_HAL_DRIVER
    RCC_OscInitTypeDef oscillator = {0};
    RCC_ClkInitTypeDef clocks = {0};
    __HAL_RCC_PWR_CLK_ENABLE();
    __HAL_PWR_VOLTAGESCALING_CONFIG(PWR_REGULATOR_VOLTAGE_SCALE1);
    oscillator.OscillatorType = RCC_OSCILLATORTYPE_HSE;
    oscillator.HSEState = RCC_HSE_ON;
    oscillator.PLL.PLLState = RCC_PLL_ON;
    oscillator.PLL.PLLSource = RCC_PLLSOURCE_HSE;
    oscillator.PLL.PLLM = 8;
    oscillator.PLL.PLLN = 336;
    oscillator.PLL.PLLP = RCC_PLLP_DIV2;
    oscillator.PLL.PLLQ = 7;
    if (HAL_RCC_OscConfig(&oscillator) != HAL_OK) StudioX_Panic(1);
    clocks.ClockType = RCC_CLOCKTYPE_SYSCLK | RCC_CLOCKTYPE_HCLK | RCC_CLOCKTYPE_PCLK1 | RCC_CLOCKTYPE_PCLK2;
    clocks.SYSCLKSource = RCC_SYSCLKSOURCE_PLLCLK;
    clocks.AHBCLKDivider = RCC_SYSCLK_DIV1;
    clocks.APB1CLKDivider = RCC_HCLK_DIV4;
    clocks.APB2CLKDivider = RCC_HCLK_DIV2;
    if (HAL_RCC_ClockConfig(&clocks, FLASH_LATENCY_5) != HAL_OK) StudioX_Panic(2);
    HAL_NVIC_SetPriorityGrouping(NVIC_PRIORITYGROUP_4);
#else
    RCC_DeInit();
    RCC_HSEConfig(RCC_HSE_ON);
    if (RCC_WaitForHSEStartUp() != SUCCESS) StudioX_Panic(1);
    RCC_APB1PeriphClockCmd(RCC_APB1Periph_PWR, ENABLE);
    PWR_MainRegulatorModeConfig(PWR_Regulator_Voltage_Scale1);
    FLASH_SetLatency(FLASH_Latency_5);
    FLASH_PrefetchBufferCmd(ENABLE);
    FLASH_InstructionCacheCmd(ENABLE);
    FLASH_DataCacheCmd(ENABLE);
    RCC_HCLKConfig(RCC_SYSCLK_Div1);
    RCC_PCLK1Config(RCC_HCLK_Div4);
    RCC_PCLK2Config(RCC_HCLK_Div2);
    RCC_PLLConfig(RCC_PLLSource_HSE, 8, 336, 2, 7);
    RCC_PLLCmd(ENABLE);
    uint32_t timeout = 2000000U;
    while (RCC_GetFlagStatus(RCC_FLAG_PLLRDY) == RESET && --timeout) { }
    if (!timeout) StudioX_Panic(2);
    RCC_SYSCLKConfig(RCC_SYSCLKSource_PLLCLK);
    timeout = 2000000U;
    while (RCC_GetSYSCLKSource() != 0x08 && --timeout) { }
    if (!timeout) StudioX_Panic(3);
    NVIC_PriorityGroupConfig(NVIC_PriorityGroup_4);
#endif
    SystemCoreClockUpdate();
    if (SystemCoreClock != BOARD_SYSCLK_HZ) StudioX_Panic(4);
#if !defined(USE_HAL_DRIVER) && !defined(STUDIOX_USE_FREERTOS)
    if (SysTick_Config(SystemCoreClock / 1000U)) StudioX_Panic(5);
#endif
}
