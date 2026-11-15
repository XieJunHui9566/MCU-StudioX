#include "workloads.h"
u32 state_machine(u32 seed,u32 count) ABI
{
    u8 i,n=(u8)(count>64UL?64UL:count),state=(u8)(seed&7UL),event;
    u32 total=seed;
    for(i=0;i<n;++i) {
        event=(u8)(inputs[i]&3U);
        switch(state) {
        case 0:total+=event;state=(u8)(event+1U);break;
        case 1:total^=0x10203040UL;state=event?2:0;break;
        case 2:total+=17UL;state=event?3:6;break;
        case 3:total=(total<<1)^event;state=4;break;
        case 4:total^=(u32)inputs[i]<<8;state=(u8)(event+4U);break;
        case 5:total+=0x12345UL;state=7;break;
        case 6:total^=total>>3;state=0;break;
        default:total-=event;state=1;break;
        }
    }
    return total^((u32)state<<24);
}
