#include "workloads.h"
u32 ring_buffer(u32 seed,u32 count) ABI
{
    u8 buffer[16];
    u8 i,n=(u8)(count>64UL?64UL:count),head=0,tail=0,next,dropped=0;
    u32 hash=seed;
    /* 单调用内模拟生产/消费，满队列丢弃新值；不冒充并发无锁实现。 */
    for(i=0;i<n;++i) {
        next=(u8)((head+1U)&15U);
        if(next==tail) ++dropped;
        else {buffer[head]=inputs[i];head=next;}
        if((i&3U)==3U && tail!=head) {
            hash=(hash<<5)^hash^(u32)buffer[tail];
            tail=(u8)((tail+1U)&15U);
        }
    }
    while(tail!=head) {
        hash=(hash<<5)^hash^(u32)buffer[tail];
        tail=(u8)((tail+1U)&15U);
    }
    return hash^((u32)dropped<<24);
}
