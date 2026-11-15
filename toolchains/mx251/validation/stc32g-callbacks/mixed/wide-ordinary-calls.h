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
u32 m_old_pair(u32 a,u32 b) OLD;
u32 m_native_pair(u32 a,u32 b) ABI;
u32 m_mutate(u32 PTR *p,u32 PTR *q,u32 v) OLD;
u32 m_old_many(u8 a,u16 b,u32 c,s8 d,u8 PTR *e,s16 f,u32 g,u8 h) OLD;
u32 PTR *m_choose_pointer(u32 PTR *p,u32 PTR *q,u8 select) OLD;
float m_old_float(float a,float b,u8 select) OLD;
float m_native_float(float a,float b,u8 select) ABI;
void m_old_store(volatile u32 PTR *p,u32 a,u32 b) OLD;
u32 m_legacy_bridge(u32 a,u32 b) OLD;
u32 m_callback_value(u32 a,u32 b) OLD;
float m_forward_float(float a,float b,u8 select) OLD;
u32 m_recursive_value(u32 a,u32 b,u8 n) OLD;
u32 m_c_pair(u32 a,u32 b) OLD;
u32 m_c_native(u32 a,u32 b) OLD;
u32 m_c_recur(u32 a,u32 b) OLD;
u32 m_c_alias(u32 a,u32 b) OLD;
u32 m_c_many(u32 a,u32 b) OLD;
u32 m_c_pointer(u32 a,u32 b) OLD;
u32 m_c_float(u32 a,u32 b) OLD;
u32 m_c_pressure(u32 a,u32 b) OLD;
u32 m_c_frame(u32 a,u32 b) OLD;
u32 m_c_callback(u32 a,u32 b) OLD;
#endif
