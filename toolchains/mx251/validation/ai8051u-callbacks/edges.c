#include "indirect.h"
typedef u8 (*ByteFn)(u8,u8) ABI;
typedef u16 (*WordFn)(u16,u16) ABI;
typedef u32 (*ZeroFn)(void) ABI;
typedef void (*VoidFn)(u32 PTR *,u32) ABI;
u8 edge_byte(u8 a,u8 b) ABI {return a^b;}
u16 edge_word(u16 a,u16 b) ABI {return a+b;}
u32 edge_zero(void) ABI {return 0x76543210UL;}
void edge_void(u32 PTR *p,u32 a) ABI {*p=a;}
static ByteFn volatile byte_fn=edge_byte;
static WordFn volatile word_fn=edge_word;
static ZeroFn volatile zero_fn=edge_zero;
static VoidFn volatile void_fn=edge_void;
static type_n_old_pair volatile __xdata far_target=n_old_pair;
/* 普通 ABI 接收函数指针；目标 ABI 由完整指针类型确定。 */
static u32 stack_target(type_n_old_pair fn,u32 a,u32 b) __reentrant
{return fn(a,b)+a;}
static u32 literal_args(u32 a,u32 b) ABI
{return far_target(a,0x81234567UL)^far_target(0x12345678UL,b);}
u8 edge_checks(void) __reentrant
{
    u32 local=0;u32 a=0x12345678UL,b=0xfedcba98UL;
    if(byte_fn(0xa5,0x3c)!=0x99)return 0;
    if(word_fn(0xffffU,0x8002U)!=0x8001U)return 0;
    if(zero_fn()!=0x76543210UL)return 0;
    void_fn(&local,0x89abcdefUL);if(local!=0x89abcdefUL)return 0;
    if(stack_target(far_target,a,b)!=(((a^b)+0x87654321UL)+a))return 0;
    if(literal_args(a,b)!=(((a^0x81234567UL)+0x87654321UL)^((0x12345678UL^b)+0x87654321UL)))return 0;
    return 1;
}
