#include "workloads.h"
u32 copy_xor(u32 seed,u32 count) ABI
{
    u8 i,n=(u8)(count>64UL?64UL:count),key=(u8)seed;
    u32 sum=seed;
    for(i=0;i<n;++i) output[i]=(u8)(inputs[i]^key);
    for(i=0;i<n;++i) sum+=(u32)output[i];
    return sum;
}
