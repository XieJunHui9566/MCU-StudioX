#ifndef STUDIOX_BOARD_H
#define STUDIOX_BOARD_H
#include <stdint.h>
#ifdef USE_HAL_DRIVER
#include "stm32f4xx_hal.h"
#else
#include "stm32f4xx.h"
#endif

/* 默认板级条件：3.3 V 供电、外部 8 MHz 无源晶振；不假设 LED/串口接线。 */
#define BOARD_SYSCLK_HZ 168000000U
extern volatile uint32_t app_error_code;
void BoardClock_Init(void);
void StudioX_Panic(uint32_t code);
void Board_Delay(uint32_t milliseconds);
#endif
