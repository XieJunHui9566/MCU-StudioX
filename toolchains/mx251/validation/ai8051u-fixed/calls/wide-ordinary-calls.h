#ifndef WIDE_ORDINARY_CALLS_H
#define WIDE_ORDINARY_CALLS_H
#if defined(__SDCC)
typedef unsigned long u32;
#define OLD __reentrant
#define ABI __reentrant __sdcccall(4)
#define PTR __xdata
#elif defined(__C251__)
typedef unsigned long u32;
#define OLD reentrant
#define ABI reentrant
#define PTR far
#else
#include <stdint.h>
typedef uint32_t u32;
#define OLD
#define ABI
#define PTR
#endif
typedef unsigned char u8;
typedef unsigned short u16;
typedef signed char s8;
typedef signed short s16;
typedef union {u32 u;float f;} Bits;
u32 old_pair(u32 a,u32 b) OLD;
u32 native_pair(u32 a,u32 b) ABI;
u32 mutate(u32 PTR *p,u32 PTR *q,u32 v) OLD;
u32 old_many(u8 a,u16 b,u32 c,s8 d,u8 PTR *e,s16 f,u32 g,u8 h) OLD;
u32 PTR *choose_pointer(u32 PTR *p,u32 PTR *q,u8 select) OLD;
float old_float(float a,float b,u8 select) OLD;
float native_float(float a,float b,u8 select) ABI;
void old_store(volatile u32 PTR *p,u32 a,u32 b) OLD;
u32 legacy_bridge(u32 a,u32 b) OLD;
u32 callback_value(u32 a,u32 b) OLD;
float forward_float(float a,float b,u8 select) OLD;
u32 recursive_value(u32 a,u32 b,u8 n) OLD;
u32 c_pair(u32 a,u32 b) OLD;
u32 c_native(u32 a,u32 b) OLD;
u32 c_recur(u32 a,u32 b) OLD;
u32 c_alias(u32 a,u32 b) OLD;
u32 c_many(u32 a,u32 b) OLD;
u32 c_pointer(u32 a,u32 b) OLD;
u32 c_float(u32 a,u32 b) OLD;
u32 c_pressure(u32 a,u32 b) OLD;
u32 c_frame(u32 a,u32 b) OLD;
u32 c_callback(u32 a,u32 b) OLD;
#endif
