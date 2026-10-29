#include "../include/AI8051U.h"
#include "../include/AI8051U_Delay.h"

#ifndef MX251_IRC_HZ
#error Save the IRC frequency in project settings before building Delay_MS
#endif
#if MX251_IRC_HZ < 1000000 || MX251_IRC_HZ > 48000000
#error Delay_MS supports IRC frequencies from 1 to 48 MHz
#endif

/* AI8051U 源模式、WTST=0：内圈 SUB 1 拍、JNE 跳转3拍/退出1拍。
 * 每个非末尾毫秒：MOV 1 + (4*N-2) + NOP 1 + SUB 1 + JNE 3
 * = 4*(N+1) 拍。四舍五入到4拍；最后一次外圈少2拍。
 * ABI2 首参数 DPH:DPL；保存 DR0，整个函数不依赖 C 优化分配。
 * 仅这一经过审核的底层函数使用 naked，调用者无需接触汇编。 */
/* 不让汇编器计算含32位数的表达式（其立即数表达式会截断）。
 * 工程保存时生成字面量，并由C编译期独立校验，手改错误也不会发布固件。 */
#ifndef MX251_DELAY_TICKS
#error Save the IRC frequency in project settings to generate MX251_DELAY_TICKS
#endif
_Static_assert(MX251_DELAY_TICKS == ((MX251_IRC_HZ + 2000L) / 4000L - 1),
               "Delay tick count does not match IRC frequency");

void Delay_MS(uint16_t milliseconds) __sdcccall(2) __naked
{
    __asm push dr0 mov r0, dph mov r1, dpl cmp wr0, #0 je 00003 $ push _WTST mov _WTST,
# 0 00001 $ : mov wr2,
#MX251_DELAY_TICKS 00002 $ : sub wr2,
# 1 jne 00002 $ nop sub wr0,
# 1 jne 00001 $ pop _WTST 00003 $
        : pop dr0 eret __endasm;
}
