/* 寄存器模型，仅用于宿主执行生成代码，不连接任何芯片。 */
#ifndef TEST_ALTA_H
#define TEST_ALTA_H
#include <stdint.h>
#include <stdbool.h>

typedef struct
{
    uint32_t CLK_CNTL, PBUS_DIVIDER;
} SYS_ControlTypeDef;

typedef struct
{
    uint32_t HSI_FREQUENCY, HSE_FREQUENCY, PLL_FREQUENCY, EXT_FREQUENCY;
} SYS_ClocksTypeDef;

typedef struct
{
    uint32_t Direction, Data, Hardware;
} GPIO_TypeDef;

extern SYS_ControlTypeDef test_sys;
extern GPIO_TypeDef test_gpio;
extern SYS_ClocksTypeDef SYS_Clocks;
#define SYS (&test_sys)
#define GPIO4 (&test_gpio)
#define GPIO_BIT4 (1u << 4)
#define APB_MASK_GPIO4 (1u << 4)
#define SYS_CLK_SOURCE_MASK 3u
#define SYS_CLK_SOURCE_HSI 0u
#define SYS_CLK_SOURCE_HSE 1u
#define SYS_CLK_SOURCE_PLL 2u
#define SYS_CLK_SOURCE_EXT 3u
#define SYS_CLK_HSE_ON (1u << 2)
#define SYS_CLK_HSE_BYP (1u << 3)
#define SYS_CLK_HSE_RDY (1u << 4)
#define SYS_CLK_PLL_ON (1u << 5)
#define SYS_CLK_PLL_RDY (1u << 6)
#define SYS_SCLK_DIV_HIGH(d) ((d) << 8)
#define SYS_SCLK_DIV_LOW(d) ((d) << 12)
#define SYS_SCLK_DIV_HIGH_MASK (15u << 8)
#define SYS_SCLK_DIV_LOW_MASK (15u << 12)
#define BOARD_EXT_FREQUENCY 50000000u
#define FLASH_MAX_FREQ 100000000u
#define read_csr(reg) test_read_csr(#reg)
uint32_t test_read_csr(const char *reg);

static inline void SYS_EnableAPBClock(uint32_t mask)
{
    (void)mask;
}

static inline void GPIO_SetSoftwareMode(GPIO_TypeDef *gpio, uint8_t bit)
{
    gpio->Hardware &= ~bit;
}

static inline void GPIO_SetLow(GPIO_TypeDef *gpio, uint8_t bit)
{
    gpio->Data &= ~bit;
}

static inline void GPIO_SetInput(GPIO_TypeDef *gpio, uint8_t bit)
{
    gpio->Direction &= ~bit;
}

static inline void GPIO_SetOutput(GPIO_TypeDef *gpio, uint8_t bit)
{
    gpio->Direction |= bit;
}
#endif
