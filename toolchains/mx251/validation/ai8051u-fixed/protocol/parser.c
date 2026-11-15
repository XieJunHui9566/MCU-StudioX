#include "app.h"
void parser_init(Parser PTR *p,u32 seed) API
{
    p->stage=0;p->length=0;p->pos=0;p->crc=0;p->wire=0;
    p->accepted=0;p->rejected=0;p->total=0;p->digest=seed;
}
void parser_feed(Parser PTR *p,u8 value) API
{
    u8 i;
    switch(p->stage){
    case 0:if(value==0xa5U)p->stage=1;break;
    case 1:
        if(value>16U){++p->rejected;p->stage=0;break;}
        p->length=value;p->pos=0;p->crc=crc_step(0xffffU,value);p->stage=value?2U:3U;break;
    case 2:
        p->payload[p->pos++]=value;p->crc=crc_step(p->crc,value);
        if(p->pos==p->length)p->stage=3;
        break;
    case 3:p->wire=(u16)value<<8;p->stage=4;break;
    default:
        p->wire|=value;
        if(p->wire==p->crc){
            ++p->accepted;p->total+=p->length;
            for(i=0;i<p->length;++i)p->digest=(p->digest<<5)^(p->digest>>27)^p->payload[i];
            p->digest^=p->length;
        }else ++p->rejected;
        p->stage=0;break;
    }
}
