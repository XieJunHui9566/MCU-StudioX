#include <stdint.h>
#include "py32f4xx.h"
#include "py32f410_ll_rcc.h"

volatile uint32_t app_counter;

int main(void)
{
    LL_RCC_HSI_Enable();
    while (LL_RCC_HSI_IsReady() != 1U) { }
    for (;;) {
        ++app_counter;
        __NOP();
    }
}
