#include "workloads.h"
typedef struct {u8 tag;u16 sequence;u32 value;} Record;
u32 struct_records(u32 seed,u32 count) ABI
{
    Record records[4];
    Record *p;
    u8 i,n=(u8)(count>64UL?64UL:count);
    u32 hash=seed;
    for(i=0;i<4U;++i) {records[i].tag=i;records[i].sequence=(u16)(seed+i);records[i].value=seed+(u32)i;}
    for(i=0;i<n;++i) {
        p=&records[i&3U];
        p->tag^=inputs[i];
        p->sequence=(u16)(p->sequence+(u16)inputs[i]+1U);
        p->value=(p->value<<1)^(u32)p->sequence^(u32)p->tag;
    }
    for(i=0;i<4U;++i)hash^=records[i].value+((u32)records[i].sequence<<8)+records[i].tag;
    return hash;
}
