#include "../indirect.h"
#include "wide-ordinary-calls.h"
u32 n_old_pair(u32 a,u32 b) OLD {return (a^b)+0x87654321UL;}
u32 n_native_pair(u32 a,u32 b) ABI {return (a+b)^b;}
u32 n_mutate(u32 PTR *p,u32 PTR *q,u32 v) OLD {*p+=v;*q^=*p;return *p^*q;}
u32 n_old_many(u8 a,u16 b,u32 c,s8 d,u8 PTR *e,s16 f,u32 g,u8 h) OLD
{return a+((u32)b<<8)+c+(u32)d+*e+(u32)f+g+((u32)h<<24);}
u32 PTR *n_choose_pointer(u32 PTR *p,u32 PTR *q,u8 select) OLD {return select?p:q;}
float n_old_float(float a,float b,u8 select) OLD {return select?a:b;}
float n_native_float(float a,float b,u8 select) ABI {return select?a:b;}
void n_old_store(volatile u32 PTR *p,u32 a,u32 b) OLD {*p=(a+b)^0x12345678UL;}
u32 n_legacy_bridge(u32 a,u32 b) OLD {return (fp_n_callback_value)(b,a)+a;}
u32 n_c_float(u32 a,u32 b) OLD
{Bits x,y,z;x.u=a;y.u=b;z.f=(fp_n_forward_float)(x.f,y.f,(u8)(a&1U));return z.u^a^b;}
