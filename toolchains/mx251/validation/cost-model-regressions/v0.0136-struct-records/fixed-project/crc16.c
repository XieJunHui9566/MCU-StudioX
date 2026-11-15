#include "workloads.h"
u32 crc16(u32 seed,u32 count) ABI
{
    u8 i,j,n=(u8)(count>64UL?64UL:count);
    u16 crc=(u16)seed;
    for(i=0;i<n;++i) {
        crc^=(u16)((u16)inputs[i]<<8);
        for(j=0;j<8U;++j) {
            if(crc&0x8000U)crc=(u16)((crc<<1)^0x1021U);
            else crc=(u16)(crc<<1);
        }
    }
    return (u32)crc;
}
