#include "wide-ordinary-calls.h"
u32 c_pair(u32 a,u32 b) OLD
{u32 x=a+0x12345678UL;return old_pair(b,a)+x+b;}
u32 c_native(u32 a,u32 b) OLD
{return native_pair(b,a)+a+b;}
u32 recursive_value(u32 a,u32 b,u8 n) OLD
{if(!n)return a^b;return recursive_value(b,a^b,(u8)(n-1U))+a+old_pair(b,a);}
u32 c_recur(u32 a,u32 b) OLD
{return recursive_value(a,b,(u8)(a&3U))^b;}
u32 c_alias(u32 a,u32 b) OLD
{u32 x=mutate(&a,(b&1U)?&a:&b,b);return x+a+b;}
u32 c_many(u32 a,u32 b) OLD
{struct {u8 before,x,after;} local;u32 v;local.before=0x5a;local.x=(u8)(a^b);local.after=0xa5;
 v=old_many((u8)a,(u16)b,a,(s8)b,&local.x,(s16)a,b,(u8)(a>>24));
 return v+a+b+(local.before^0x5aU)+(local.after^0xa5U);}
u32 c_pointer(u32 a,u32 b) OLD
{struct {u32 x,y;} local;u32 PTR *p;local.x=a;local.y=b;
 p=choose_pointer(&local.x,&local.y,(u8)(b&1U));mutate(p,&local.x,b);return *p+local.x+local.y;}
float forward_float(float a,float b,u8 select) OLD
{float x=old_float(a,b,select);return native_float(x,a,(u8)(select^1U));}
u32 c_pressure(u32 a,u32 b) OLD
{u32 v0=a+1UL,v1=a+0x12345678UL,v2=b^0xa5a5a5a5UL,v3=b+0x10001UL;
 u32 v4=a^0xdeadbeefUL,v5=a+b,v6=a-b,v7=b+0x87654321UL;
 u32 x=old_pair(a,b),y=native_pair(b,a),z=old_pair(x,y);
 return z+v0+v1+v2+v3+v4+v5+v6+v7;}
u32 c_frame(u32 a,u32 b) OLD
{volatile u32 local[3];local[0]=0x13579bdfUL;local[1]=a;local[2]=0x2468ace0UL;
 old_store(&local[1],a,b);return local[1]^(local[0]^0x13579bdfUL)^(local[2]^0x2468ace0UL);}
u32 callback_value(u32 a,u32 b) OLD {return (a+b)^(a<<3);}
u32 c_callback(u32 a,u32 b) OLD {return legacy_bridge(a,b)^a^b;}
