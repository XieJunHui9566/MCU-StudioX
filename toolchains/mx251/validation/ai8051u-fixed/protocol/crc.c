#include "app.h"
u16 crc_step(u16 crc,u8 value) API
{
    u8 iteration;
    crc^=(u16)value<<8;
    for(iteration=0;iteration<8U;++iteration)crc=(crc&0x8000U)?(u16)((crc<<1)^0x1021U):(u16)(crc<<1);
    return crc;
}
