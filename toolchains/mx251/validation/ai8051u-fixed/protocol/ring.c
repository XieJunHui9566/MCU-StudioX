#include "app.h"
void ring_init(Ring PTR *r) API {r->head=0;r->tail=0;r->count=0;r->dropped=0;}
void ring_push(Ring PTR *r,u8 value) API
{
    if(r->count==32U){++r->dropped;return;}
    r->bytes[r->head]=value;r->head=(r->head+1U)&31U;++r->count;
}
u8 ring_pop(Ring PTR *r,u8 PTR *value) API
{
    if(r->count==0U)return 0;
    *value=r->bytes[r->tail];r->tail=(r->tail+1U)&31U;--r->count;return 1;
}
