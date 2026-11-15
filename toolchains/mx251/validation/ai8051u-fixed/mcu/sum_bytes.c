#include "workloads.h"
u32 sum_bytes(u32 seed,u32 count) ABI
{
    u8 i,n=(u8)(count>64UL?64UL:count);
    u32 sum=seed;
    for(i=0;i<n;++i) sum+=(u32)inputs[i];
    return sum;
}
