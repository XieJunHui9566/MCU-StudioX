#ifndef STUDIOX_BYTE_UTILS_H
#define STUDIOX_BYTE_UTILS_H

#include <stdint.h>

/* 输入必须指向至少四个字节；按字节访问，允许未对齐数据。 */
static inline uint32_t studiox_read_le32(const uint8_t *bytes)
{
    return (uint32_t)bytes[0] | ((uint32_t)bytes[1] << 8)
        | ((uint32_t)bytes[2] << 16) | ((uint32_t)bytes[3] << 24);
}

#endif
