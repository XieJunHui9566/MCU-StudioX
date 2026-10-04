#include "StudioX_System.h"
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

SYS_ControlTypeDef test_sys;
GPIO_TypeDef test_gpio;
SYS_ClocksTypeDef SYS_Clocks;
static uint64_t cycles;
static uint64_t step = 10000u;
static bool mapped, convert;
static unsigned checks;

uint32_t test_read_csr(const char *reg)
{
    if (strcmp(reg, "mcycleh") == 0)
        return (uint32_t)(cycles >> 32);
    cycles += step;
    if (mapped && convert)
    {
        ADC_TypeDef *adcs[] = {ADC0, ADC1, ADC2};
        for (unsigned i = 0; i < 3; ++i)
        {
            if ((adcs[i]->CTRL & (ADC_CTRL_START | ADC_CTRL_STOP)) == ADC_CTRL_START)
            {
                adcs[i]->STAT = ADC_STAT_EOC;
                adcs[i]->DATA = 0xfabcu;
                adcs[i]->CTRL &= ~ADC_CTRL_START;
            }
        }
    }
    return (uint32_t)cycles;
}

static void check(bool ok, const char *name)
{
    if (!ok)
    {
        fprintf(stderr, "FAIL %s\n", name);
        exit(1);
    }
    ++checks;
    printf("PASS %s\n", name);
}

static void reset(void)
{
    memset((void *)ADC0, 0, 0x6000);
    SYS->CLK_CNTL = SYS_CLK_SOURCE_PLL | SYS_CLK_PLL_ON | SYS_CLK_PLL_RDY;
    SYS_Clocks.PLL_FREQUENCY = 100000000u;
    SYS->PBUS_DIVIDER = 1u;
    cycles = 0u;
    step = 10u;
    convert = true;
}

int main(void)
{
    void *memory = VirtualAlloc((void *)ADC0, 0x10000, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
    check(memory == (void *)ADC0, "isolated virtual registers allocated, no hardware");
    mapped = true;
    uint16_t value = 0x5678;
    reset();
    check(StudioX_ADC_Read(NULL, ADC_CHANNEL0, 100, &value) == STUDIOX_ANALOG_INVALID_ARGUMENT &&
              value == 0x5678,
          "invalid ADC preserves output");
    check(StudioX_ADC_Read(ADC0, ADC_CHANNEL0, 0, &value) == STUDIOX_ANALOG_INVALID_ARGUMENT &&
              ADC0->CTRL == 0,
          "zero timeout does not start ADC");
    check(StudioX_ADC_Read(ADC0, ADC_CHANNEL0, 100, NULL) == STUDIOX_ANALOG_INVALID_ARGUMENT,
          "null result rejected");
    check(StudioX_ADC_Read(ADC0, ADC_CHANNEL1, 100, &value) == STUDIOX_ANALOG_INVALID_ARGUMENT,
          "unreserved external channel rejected");
    check(StudioX_ADC_Read(ADC0, ADC_CHANNEL16, 100, &value) == STUDIOX_ANALOG_INVALID_ARGUMENT,
          "temperature restricted to ADC1");
    check(StudioX_ADC_Read(ADC0, (ADC_ChannelNumTypeDef)0, 100, &value) ==
                  STUDIOX_ANALOG_INVALID_ARGUMENT &&
              StudioX_ADC_Read(ADC0, (ADC_ChannelNumTypeDef)99, 100, &value) ==
                  STUDIOX_ANALOG_INVALID_ARGUMENT,
          "channel bounds checked before mask shift");
    ADC0->STAT = ADC_STAT_EN;
    ADC0->CTRL = ADC_CTRL_DMAEN;
    check(StudioX_ADC_Read(ADC0, ADC_CHANNEL0, 100, &value) == STUDIOX_ANALOG_BUSY &&
              ADC0->CTRL == ADC_CTRL_DMAEN,
          "busy ADC leaves DMA state unchanged");
    reset();
    ADC0->STAT = ADC_STAT_EOC;
    ADC0->DATA = 7;
    check(StudioX_ADC_Read(ADC0, ADC_CHANNEL0, 100, &value) == STUDIOX_ANALOG_OK && value == 0xabc,
          "old completion is cleared; newly converted 12-bit result returned");
    check(ADC0->CHNL == ADC_SEQ_LENGTH1 && ADC0->SEQ[0] == ADC_CHANNEL0 &&
              (ADC0->CTRL >> 16) == (STUDIOX_ANALOG_SEPARATE_BUS ? 2 : 4),
          "actual analog bus produces safe SCLK divider and one sample sequence");
    reset();
    SYS_Clocks.PLL_FREQUENCY = 248000000u;
    SYS->PBUS_DIVIDER = 0;
    check(StudioX_ADC_Read(ADC2, ADC_CHANNEL0, UINT32_MAX, &value) == STUDIOX_ANALOG_OK &&
              (ADC2->CTRL >> 16) == (STUDIOX_ANALOG_SEPARATE_BUS ? 2 : 12),
          "CPU clock changes follow selected logic clock source; large timeout does not overflow");
    reset();
    check(StudioX_ADC_Read(ADC1, ADC_CHANNEL16, 100, &value) == STUDIOX_ANALOG_OK,
          "internal temperature uses ADC1 without external pin");
    reset();
    convert = false;
    value = 123;
    check(StudioX_ADC_Read(ADC0, ADC_CHANNEL0, 1, &value) == STUDIOX_ANALOG_TIMEOUT &&
              value == 123 && (ADC0->CTRL & ADC_CTRL_STOP) && cycles < 200,
          "timeout stops conversion and preserves caller output");
    reset();
    convert = false;
    cycles = UINT64_MAX - 50;
    check(StudioX_ADC_Read(ADC0, ADC_CHANNEL0, 1, &value) == STUDIOX_ANALOG_TIMEOUT,
          "timeout survives 64-bit cycle rollover");
    reset();
    SYS->CLK_CNTL = SYS_CLK_SOURCE_PLL;
    check(StudioX_ADC_Read(ADC0, ADC_CHANNEL0, 100, &value) == STUDIOX_ANALOG_CLOCK_UNAVAILABLE &&
              ADC0->CTRL == 0 && StudioX_DAC_Write(DAC0, 512) == STUDIOX_ANALOG_CLOCK_UNAVAILABLE &&
              DAC0->CTRL == 0,
          "failed PLL rejects ADC/DAC before peripheral access");
    SYS->CLK_CNTL = SYS_CLK_SOURCE_HSI | SYS_CLK_PLL_ON | SYS_CLK_PLL_RDY;
    SYS->PBUS_DIVIDER = 49u;
    check(StudioX_AnalogClockHz() == (STUDIOX_ANALOG_SEPARATE_BUS ? 50000000u : STUDIOX_HSI_HZ),
          "HSI switch follows logic clock topology independently of APB divider");
    reset();
    check(DAC0->CTRL == 0 && DAC1->CTRL == 0, "configuration alone never enables DAC output");
    check(StudioX_DAC_Write(DAC0, 0) == STUDIOX_ANALOG_OK && DAC0->DATA == 0 &&
              DAC0->CTRL == (DAC_CTRL_EN | DAC_CTRL_BUFEN),
          "DAC lower endpoint enables buffered output");
    check(StudioX_DAC_Write(DAC1, 1023) == STUDIOX_ANALOG_OK && DAC1->DATA == 1023,
          "DAC upper endpoint supported");
    check(StudioX_DAC_Write(DAC1, 1024) == STUDIOX_ANALOG_INVALID_ARGUMENT && DAC1->DATA == 1023 &&
              StudioX_DAC_Write(NULL, 0) == STUDIOX_ANALOG_INVALID_ARGUMENT,
          "out of range and invalid DAC do not write registers");
    DAC1->CTRL |= DAC_CTRL_DMAEN;
    check(StudioX_DAC_Write(DAC1, 10) == STUDIOX_ANALOG_BUSY && DAC1->DATA == 1023,
          "DAC helper does not overwrite active DMA");
    mapped = false;
    VirtualFree(memory, 0, MEM_RELEASE);
    printf("PASS %u register checks; software model only\n", checks);
    return 0;
}
