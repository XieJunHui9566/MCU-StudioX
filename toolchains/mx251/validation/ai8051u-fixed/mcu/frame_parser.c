#include "workloads.h"
u32 frame_parser(u32 seed,u32 count) ABI
{
    u8 i,n=(u8)(count>64UL?64UL:count),state=0,left=0,check=0,b;
    u16 accepted=0,rejected=0;
    u32 payload=seed;
    /* A5, 长度(1..8), payload, XOR；无效长度和校验错误回到等待帧头。 */
    for(i=0;i<n;++i) {
        b=inputs[i];
        switch(state) {
        case 0:if(b==0xa5U)state=1;break;
        case 1:
            if(b==0 || b>8U){++rejected;state=0;}
            else {left=b;check=b;state=2;}
            break;
        case 2:payload=(payload<<3)^payload^b;check^=b;if(--left==0)state=3;break;
        default:if(b==check)++accepted;else ++rejected;state=0;break;
        }
    }
    return payload^((u32)accepted<<16)^((u32)rejected<<24)^((u32)state<<8)^left;
}
