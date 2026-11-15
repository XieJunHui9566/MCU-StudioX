#define KERNEL 1
/* 同一份纯 C 内核；跨编译单元传入运行时数据，禁止常量折叠。 */
typedef unsigned long u32;
typedef unsigned int u16;
typedef unsigned char u8;
#ifndef KERNEL
#define KERNEL 0
#endif
#if KERNEL>=10
#include <math.h>
#ifdef __SDCC
#define SIN sinf
#define COS cosf
#define TAN tanf
#define ATAN atanf
#define SQRT sqrtf
#else
#define SIN sin
#define COS cos
#define TAN tan
#define ATAN atan
#define SQRT sqrt
#endif
typedef union { u32 u; float f; } bits_t;
#endif
u32 kernel1(u32 a,u32 b) __reentrant __sdcccall(4)
{
#if KERNEL==0
    return a;
#elif KERNEL==1
    return (a+b)^b;
#elif KERNEL==2
    return (a-b)^b;
#elif KERNEL==3
    return (u32)(u16)a*(u32)(u16)b;
#elif KERNEL==4
    return a*b;
#elif KERNEL==5
    return a/b;
#elif KERNEL==6
    return a%b;
#elif KERNEL==7
    return a<<(b&31U);
#elif KERNEL==8
    u8 n;
    for(n=0;n<8;++n) a=(a<<1)^((a&0x80000000UL)?0x04C11DB7UL:0UL);
    return a^b;
#elif KERNEL==9
    return (a>b)?a:b;
#else
    bits_t x,y,r;
    x.u=a; y.u=b;
#if KERNEL==10
    r.f=x.f+y.f;
#elif KERNEL==11
    r.f=x.f*y.f;
#elif KERNEL==12
    r.f=x.f/y.f;
#elif KERNEL==13
    r.f=SQRT(x.f);
#elif KERNEL==14
    r.f=SIN(x.f);
#elif KERNEL==15
    r.f=COS(x.f);
#elif KERNEL==16
    r.f=TAN(x.f);
#elif KERNEL==17
    r.f=ATAN(x.f);
#endif
    return r.u;
#endif
}
