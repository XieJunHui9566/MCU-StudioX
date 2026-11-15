#include "app.h"
void app_run(const u8 PTR *input,u16 length,u8 chunk,u32 seed,Outcome PTR *out) API
{
    struct {u32 before;Ring r;Parser p;u32 after;} local;
    u16 pos=0;
    u8 n,value;
    /* 哨兵在结构字段级检查，不按填充字节或类型别名读取。 */
    local.before=0x13579bdfUL;local.after=0x2468ace0UL;
    seed=revise_seed(seed,length);
    ring_init(&local.r);parser_init(&local.p,seed);
    if(chunk==0U)chunk=1;
    while(pos<length){
        for(n=0;n<chunk && pos<length;++n)ring_push(&local.r,input[pos++]);
        while(ring_pop(&local.r,&value))parser_feed(&local.p,value);
    }
    out->accepted=local.p.accepted;out->rejected=local.p.rejected;out->dropped=local.r.dropped;
    out->total=local.p.total;out->digest=local.p.digest;out->stage=local.p.stage;
    out->guard=(local.before^0x13579bdfUL)|(local.after^0x2468ace0UL);out->seed=seed;
}
