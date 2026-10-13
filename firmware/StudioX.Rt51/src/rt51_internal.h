#ifndef STUDIOX_RT51_INTERNAL_H
#define STUDIOX_RT51_INTERNAL_H
#include "rt51.h"
/* 只声明架构公共的 IE/EA，不能包含某一型号的 Timer2 等 SFR 地址。 */
__sfr __at(0xa8) rt51_ie;
__sbit __at(0xaf) rt51_ea;
#define IE rt51_ie
#define EA rt51_ea
extern __data uint8_t rt51_port_kind, rt51_saved_sp, rt51_from_len, rt51_to_len;
extern uint8_t __xdata * __data rt51_from;
extern uint8_t __xdata * __data rt51_to;
void rt51_select(void);
void rt51_bootstrap(void);
void rt51_port_start(void) __naked;
void rt51_trap(uint8_t fault);
#if RT51_MAX_TIMERS > 0
void rt51_timers_reset(void);
void rt51_timers_tick(void);
uint8_t rt51_timer_dispatch_owner(uint8_t task);
#endif
#endif
