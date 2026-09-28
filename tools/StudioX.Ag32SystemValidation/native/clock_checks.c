#include "StudioX_System.h"
#include <stdio.h>
#include <string.h>
#include <stdlib.h>

SYS_ControlTypeDef test_sys;
GPIO_TypeDef test_gpio;
/* PE/COFF 的弱数据符号与 RISC-V ELF 不同，由寄存器夹具提供存储。 */
SYS_ClocksTypeDef SYS_Clocks;
static uint64_t cycles;
static uint64_t step = 10000u;
static unsigned reads;
uint32_t test_read_csr(const char *reg)
{
    ++reads;
    if (strcmp(reg, "mcycleh") == 0) { return cycles >> 32; }
    cycles += step;
    return (uint32_t)cycles;
}
static void check(bool condition, const char *message)
{
    if (!condition) { fprintf(stderr, "FAIL %s\n", message); exit(1); }
    printf("PASS %s\n", message);
}
int main(void)
{
    check(StudioX_SystemStatus == STUDIOX_SYSTEM_HSE_TIMEOUT, "automatic pre-main init has bounded HSE timeout");
    check(StudioX_SystemClockHz() == STUDIOX_HSI_HZ && SYS->PBUS_DIVIDER == 0, "failed clock startup uses real HSI and safe bus");
    test_sys.CLK_CNTL = SYS_CLK_HSE_RDY;
    StudioX_SystemInit();
    check(StudioX_SystemStatus == STUDIOX_SYSTEM_PLL_TIMEOUT && (SYS->CLK_CNTL & SYS_CLK_SOURCE_MASK) == SYS_CLK_SOURCE_HSI, "PLL timeout falls back and reports cause");
    test_sys.CLK_CNTL = SYS_CLK_HSE_RDY | SYS_CLK_PLL_RDY;
    StudioX_SystemInit();
    check(StudioX_SystemStatus == STUDIOX_SYSTEM_OK && StudioX_SystemClockHz() == 200000000u && StudioX_PeripheralClockHz() == 100000000u, "generated clocks drive system and peripheral clocks");
    check(LED1.Port == GPIO4 && LED1.Bit == GPIO_BIT4 && LED1.Pin == 2u && test_gpio.Direction == GPIO_BIT4 && test_gpio.Data == 0u, "named GPIO auto initialization and physical pin are distinct");
    unsigned before_reads = reads;
    Delay_us(0); Delay_ms(0);
    check(reads == before_reads, "zero delay never waits or reads cycle counter");
    test_sys.CLK_CNTL = SYS_CLK_SOURCE_HSI;
    SYS_Clocks.HSI_FREQUENCY = 12345678u;
    cycles = 0; step = 1;
    Delay_us(1);
    check(cycles >= 14 && cycles <= 16, "fractional MHz rounds up to at least 13 cycles");
    cycles = UINT32_MAX - 5u;
    uint64_t start = cycles;
    Delay_us(2);
    check(cycles - start >= 25 && cycles - start < 32, "RV32 high-low-high read crosses low word rollover");
    cycles = UINT64_MAX - 5u; start = cycles;
    Delay_us(2);
    check((uint64_t)(cycles - start) >= 25 && (uint64_t)(cycles - start) < 32, "unsigned elapsed time crosses 64-bit rollover");
    SYS_Clocks.HSI_FREQUENCY = 1u;
    cycles = 0; step = 1000000u;
    Delay_ms(UINT32_MAX);
    check(cycles >= 5294968u && cycles < 8000000u, "large milliseconds do not overflow through ms times 1000");
    SYS_Clocks.HSI_FREQUENCY = 10000000u;
    cycles = 0; step = 100;
    Delay_1ms();
    check(cycles >= 10000u && cycles < 10300u, "one millisecond uses current clock source");
    return 0;
}
