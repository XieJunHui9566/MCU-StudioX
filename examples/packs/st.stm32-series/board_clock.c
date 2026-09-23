#include "board.h"

volatile uint32_t app_error_code;
void StudioX_Panic(uint32_t code)
{
    app_error_code = code;
    __disable_irq();
    for (;;) { __NOP(); }
}

/* 有界等待：晶振或电源配置错误时保留错误码，不静默降频。 */
static void WaitBits(volatile uint32_t *reg, uint32_t mask, uint32_t value, uint32_t error)
{
    uint32_t remaining = 4000000U;
    while ((*reg & mask) != value && --remaining) { }
    if (!remaining) StudioX_Panic(error);
}

/* 默认 3.3 V、外部 8 MHz 无源晶振。参数由器件目录生成，禁止超频。
 * 先切回 HSI，再改 PLL；HAL/SPL 共用寄存器时钟步骤，SDK 源码保持原样。
 */
void BoardClock_Init(void)
{
    RCC->CR |= RCC_CR_HSION;
    WaitBits(&RCC->CR, RCC_CR_HSIRDY, RCC_CR_HSIRDY, 1);
    RCC->CFGR &= ~RCC_CFGR_SW;
    WaitBits(&RCC->CFGR, RCC_CFGR_SWS, 0U, 2);
    RCC->CR &= ~RCC_CR_PLLON;
    WaitBits(&RCC->CR, RCC_CR_PLLRDY, 0U, 3);
    RCC->CR &= ~RCC_CR_HSEBYP;
    RCC->CR |= RCC_CR_HSEON;
    WaitBits(&RCC->CR, RCC_CR_HSERDY, RCC_CR_HSERDY, 4);
#if defined(STUDIOX_F4)
    RCC->APB1ENR |= RCC_APB1ENR_PWREN;
    (void)RCC->APB1ENR;
    PWR->CR |= PWR_CR_VOS;
    /* VOS 在 PLL 开启后生效；180 MHz 型号另需 Over-drive。 */
    FLASH->ACR = BOARD_FLASH_LATENCY | FLASH_ACR_PRFTEN | FLASH_ACR_ICEN | FLASH_ACR_DCEN;
    RCC->PLLCFGR = 4U | (BOARD_PLL_N << 6U) | (((BOARD_PLL_P / 2U) - 1U) << 16U)
        | RCC_PLLCFGR_PLLSRC_HSE | (BOARD_PLL_Q << 24U);
#if defined(RCC_PLLCFGR_PLLR) || defined(STM32F410xx)
    /* 部分 F4 具有额外 PLLR 输出；保留合法的 /2，模板 SYSCLK 使用 PLLP。 */
    RCC->PLLCFGR |= (2U << 28U);
#endif
    RCC->CFGR = (RCC->CFGR & ~(RCC_CFGR_HPRE | RCC_CFGR_PPRE1 | RCC_CFGR_PPRE2))
        | BOARD_APB1_BITS | BOARD_APB2_BITS;
#else
#if !defined(STM32F100xB) && !defined(STM32F100xE)
    FLASH->ACR = BOARD_FLASH_LATENCY | FLASH_ACR_PRFTBE;
#endif
#if defined(RCC_CFGR2_PREDIV1)
    RCC->CFGR2 = 0U; /* HSE 直接输入 PREDIV1 /1；连接型不使用 PLL2。 */
#endif
    RCC->CFGR = (RCC->CFGR & ~(RCC_CFGR_HPRE | RCC_CFGR_PPRE1 | RCC_CFGR_PPRE2
        | RCC_CFGR_PLLSRC | RCC_CFGR_PLLXTPRE | RCC_CFGR_PLLMULL | RCC_CFGR_ADCPRE))
        | RCC_CFGR_PLLSRC | BOARD_PLL_BITS | BOARD_APB1_BITS | RCC_CFGR_ADCPRE_DIV6;
#endif
    RCC->CR |= RCC_CR_PLLON;
    WaitBits(&RCC->CR, RCC_CR_PLLRDY, RCC_CR_PLLRDY, 5);
#if defined(STUDIOX_OVERDRIVE)
    PWR->CR |= PWR_CR_ODEN;
    WaitBits(&PWR->CSR, PWR_CSR_ODRDY, PWR_CSR_ODRDY, 6);
    PWR->CR |= PWR_CR_ODSWEN;
    WaitBits(&PWR->CSR, PWR_CSR_ODSWRDY, PWR_CSR_ODSWRDY, 7);
#endif
    RCC->CFGR = (RCC->CFGR & ~RCC_CFGR_SW) | RCC_CFGR_SW_PLL;
    WaitBits(&RCC->CFGR, RCC_CFGR_SWS, RCC_CFGR_SWS_PLL, 8);
    SystemCoreClockUpdate();
    if (SystemCoreClock != BOARD_SYSCLK_HZ) StudioX_Panic(9);
#ifdef USE_HAL_DRIVER
    HAL_NVIC_SetPriorityGrouping(NVIC_PRIORITYGROUP_4);
    if (HAL_InitTick(TICK_INT_PRIORITY) != HAL_OK) StudioX_Panic(15);
#else
    NVIC_PriorityGroupConfig(NVIC_PriorityGroup_4);
#ifndef STUDIOX_USE_FREERTOS
    if (SysTick_Config(SystemCoreClock / 1000U)) StudioX_Panic(15);
#endif
#endif
}
