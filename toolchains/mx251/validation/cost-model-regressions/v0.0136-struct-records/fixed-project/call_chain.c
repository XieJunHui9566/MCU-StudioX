#include "workloads.h"
u32 call_chain(u32 seed,u32 count) ABI
{
    u8 i,n=(u8)(count>64UL?64UL:count);
    u32 acc=seed;
    for(i=0;i<n;++i) acc=mix4(acc,(u32)inputs[i],(u32)i,seed);
    return acc;
}
