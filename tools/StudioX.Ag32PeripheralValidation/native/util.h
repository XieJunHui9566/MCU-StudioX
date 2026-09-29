/* 只替代宿主不存在的 MCU 寄存器类型；analog_ip.h 使用原厂原始字节。 */
#pragma once
#include "alta.h"
#include <stddef.h>
#define __IO volatile
#define MODIFY_REG(reg, mask, value) ((reg) = ((reg) & ~(mask)) | (value))
