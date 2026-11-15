#ifndef MATH_PORT_H
#define MATH_PORT_H
#ifdef __C251__
typedef unsigned char uint8_t;
typedef unsigned int uint16_t;
typedef unsigned long uint32_t;
typedef long int32_t;
#define DATA data
#define XDATA xdata
#define CODE code
#define sqrtf sqrt
#define sinf sin
#define cosf cos
#define tanf tan
#define atanf atan
#else
#include <stdint.h>
#define DATA __data
#define XDATA __xdata
#define CODE __code
#endif
typedef char require_binary32[(sizeof(float)==4 && sizeof(double)==4 && sizeof(uint32_t)==4)?1:-1];
extern volatile uint32_t DATA math_b;
extern volatile uint32_t DATA math_rem;
typedef uint32_t (*kernel_t)(uint32_t);
uint32_t sw_identity(uint32_t);
uint32_t sw_umul(uint32_t);
uint32_t sw_udiv(uint32_t);
uint32_t sw_sdiv(uint32_t);
uint32_t sw_fadd(uint32_t);
uint32_t sw_fsub(uint32_t);
uint32_t sw_fmul(uint32_t);
uint32_t sw_fdiv(uint32_t);
uint32_t sw_sqrt(uint32_t);
uint32_t sw_compare(uint32_t);
uint32_t sw_classify(uint32_t);
uint32_t sw_ftoi(uint32_t);
uint32_t sw_itof(uint32_t);
uint32_t sw_sin(uint32_t);
uint32_t sw_cos(uint32_t);
uint32_t sw_tan(uint32_t);
uint32_t sw_atan(uint32_t);
#endif
