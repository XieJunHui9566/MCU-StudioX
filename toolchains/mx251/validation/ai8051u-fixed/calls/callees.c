#include "wide-ordinary-calls.h"
u32 old_pair(u32 a,u32 b) OLD {return (a^b)+0x87654321UL;}
u32 native_pair(u32 a,u32 b) ABI {return (a+b)^b;}
u32 mutate(u32 PTR *p,u32 PTR *q,u32 v) OLD {*p+=v;*q^=*p;return *p^*q;}
u32 old_many(u8 a,u16 b,u32 c,s8 d,u8 PTR *e,s16 f,u32 g,u8 h) OLD
{return a+((u32)b<<8)+c+(u32)d+*e+(u32)f+g+((u32)h<<24);}
u32 PTR *choose_pointer(u32 PTR *p,u32 PTR *q,u8 select) OLD {return select?p:q;}
float old_float(float a,float b,u8 select) OLD {return select?a:b;}
float native_float(float a,float b,u8 select) ABI {return select?a:b;}
void old_store(volatile u32 PTR *p,u32 a,u32 b) OLD {*p=(a+b)^0x12345678UL;}
u32 legacy_bridge(u32 a,u32 b) OLD {return callback_value(b,a)+a;}
u32 c_float(u32 a,u32 b) OLD
{Bits x,y,z;x.u=a;y.u=b;z.f=forward_float(x.f,y.f,(u8)(a&1U));return z.u^a^b;}
