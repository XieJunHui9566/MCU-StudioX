#include <math.h>
#include "math_port.h"
volatile uint32_t DATA math_b;
volatile uint32_t DATA math_rem;
typedef union { uint32_t u; int32_t s; float f; } number_t;
uint32_t sw_identity(uint32_t x) { return x; }
uint32_t sw_umul(uint32_t x) { return x * math_b; }
uint32_t sw_udiv(uint32_t x) { math_rem=x % math_b; return x / math_b; }
uint32_t sw_sdiv(uint32_t x) { math_rem=(int32_t)x % (int32_t)math_b; return (int32_t)x / (int32_t)math_b; }
uint32_t sw_fadd(uint32_t x) { number_t a,b,r; a.u=x; b.u=math_b; r.f=a.f+b.f; return r.u; }
uint32_t sw_fsub(uint32_t x) { number_t a,b,r; a.u=x; b.u=math_b; r.f=a.f-b.f; return r.u; }
uint32_t sw_fmul(uint32_t x) { number_t a,b,r; a.u=x; b.u=math_b; r.f=a.f*b.f; return r.u; }
uint32_t sw_fdiv(uint32_t x) { number_t a,b,r; a.u=x; b.u=math_b; r.f=a.f/b.f; return r.u; }
uint32_t sw_sqrt(uint32_t x) { number_t a,r; a.u=x; r.f=sqrtf(a.f); return r.u; }
uint32_t sw_sin(uint32_t x) { number_t a,r; a.u=x; r.f=sinf(a.f); return r.u; }
uint32_t sw_cos(uint32_t x) { number_t a,r; a.u=x; r.f=cosf(a.f); return r.u; }
uint32_t sw_tan(uint32_t x) { number_t a,r; a.u=x; r.f=tanf(a.f); return r.u; }
uint32_t sw_atan(uint32_t x) { number_t a,r; a.u=x; r.f=atanf(a.f); return r.u; }
uint32_t sw_ftoi(uint32_t x) { number_t a; a.u=x; return (int32_t)a.f; }
uint32_t sw_itof(uint32_t x) { number_t r; r.f=(int32_t)x; return r.u; }
uint32_t sw_compare(uint32_t x) { return x; }
uint32_t sw_classify(uint32_t x) { return x; }
