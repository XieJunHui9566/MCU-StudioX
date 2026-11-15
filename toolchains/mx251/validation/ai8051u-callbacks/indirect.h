#ifndef MX251_INDIRECT_H
#define MX251_INDIRECT_H
#include "native/wide-ordinary-calls.h"
typedef u32  (*type_n_old_pair)(u32 a,u32 b) OLD;
extern type_n_old_pair volatile fp_n_old_pair;
typedef u32  (*type_n_native_pair)(u32 a,u32 b) ABI;
extern type_n_native_pair volatile fp_n_native_pair;
typedef u32  (*type_n_mutate)(u32 PTR *p,u32 PTR *q,u32 v) OLD;
extern type_n_mutate volatile fp_n_mutate;
typedef u32  (*type_n_old_many)(u8 a,u16 b,u32 c,s8 d,u8 PTR *e,s16 f,u32 g,u8 h) OLD;
extern type_n_old_many volatile fp_n_old_many;
typedef u32 PTR * (*type_n_choose_pointer)(u32 PTR *p,u32 PTR *q,u8 select) OLD;
extern type_n_choose_pointer volatile fp_n_choose_pointer;
typedef float  (*type_n_old_float)(float a,float b,u8 select) OLD;
extern type_n_old_float volatile fp_n_old_float;
typedef float  (*type_n_native_float)(float a,float b,u8 select) ABI;
extern type_n_native_float volatile fp_n_native_float;
typedef void  (*type_n_old_store)(volatile u32 PTR *p,u32 a,u32 b) OLD;
extern type_n_old_store volatile fp_n_old_store;
typedef u32  (*type_n_legacy_bridge)(u32 a,u32 b) OLD;
extern type_n_legacy_bridge volatile fp_n_legacy_bridge;
typedef u32  (*type_n_callback_value)(u32 a,u32 b) OLD;
extern type_n_callback_value volatile fp_n_callback_value;
typedef float  (*type_n_forward_float)(float a,float b,u8 select) OLD;
extern type_n_forward_float volatile fp_n_forward_float;
typedef u32  (*type_n_recursive_value)(u32 a,u32 b,u8 n) OLD;
extern type_n_recursive_value volatile fp_n_recursive_value;
typedef u32  (*type_n_c_pair)(u32 a,u32 b) OLD;
extern type_n_c_pair volatile fp_n_c_pair;
typedef u32  (*type_n_c_native)(u32 a,u32 b) OLD;
extern type_n_c_native volatile fp_n_c_native;
typedef u32  (*type_n_c_recur)(u32 a,u32 b) OLD;
extern type_n_c_recur volatile fp_n_c_recur;
typedef u32  (*type_n_c_alias)(u32 a,u32 b) OLD;
extern type_n_c_alias volatile fp_n_c_alias;
typedef u32  (*type_n_c_many)(u32 a,u32 b) OLD;
extern type_n_c_many volatile fp_n_c_many;
typedef u32  (*type_n_c_pointer)(u32 a,u32 b) OLD;
extern type_n_c_pointer volatile fp_n_c_pointer;
typedef u32  (*type_n_c_float)(u32 a,u32 b) OLD;
extern type_n_c_float volatile fp_n_c_float;
typedef u32  (*type_n_c_pressure)(u32 a,u32 b) OLD;
extern type_n_c_pressure volatile fp_n_c_pressure;
typedef u32  (*type_n_c_frame)(u32 a,u32 b) OLD;
extern type_n_c_frame volatile fp_n_c_frame;
typedef u32  (*type_n_c_callback)(u32 a,u32 b) OLD;
extern type_n_c_callback volatile fp_n_c_callback;
u8 edge_checks(void) __reentrant;
#endif
