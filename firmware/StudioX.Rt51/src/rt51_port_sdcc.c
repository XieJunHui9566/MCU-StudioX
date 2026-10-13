#include "rt51_internal.h"

/* EA 的读取与清零之间不依赖 C 临时变量；ISR 可打断读取但必须保留寄存器。 */
uint8_t rt51_critical_enter(void) __naked
{
    __asm
        mov c, _rt51_ea
        clr _rt51_ea
        clr a
        rlc a
        mov dpl, a
        ret
    __endasm;
}

void rt51_yield(void) __naked
{
    __asm
        .globl _rt51_port_tick
        .globl _rt51_port_start
        push _rt51_ie
        clr _rt51_ea
        push acc
        mov a, #0
        sjmp rt51_save

_rt51_port_tick:
        push _rt51_ie
        clr _rt51_ea
        push acc
        mov a, #1

rt51_save:
        mov _rt51_port_kind, a
        push b
        push dpl
        push dph
        push psw
        push 0x00
        push 0x01
        push 0x02
        push 0x03
        push 0x04
        push 0x05
        push 0x06
        push 0x07
        push _bp
        push _rt51_compiler_bits
        mov _rt51_saved_sp, sp
        mov a, psw
        anl a, #0x18
        jz rt51_bank_ok
        mov _rt51_fault, #2
        ljmp rt51_fatal
rt51_bank_ok:
        mov psw, #0
        mov sp, #0x2f
        lcall _rt51_select
        sjmp rt51_copy

_rt51_port_start:
        mov _rt51_port_kind, #2
        mov psw, #0
        mov sp, #0x2f
        lcall _rt51_select

rt51_copy:
        mov a, sp
        cjne a, #0x2f, rt51_kernel_failure
        mov r0, #0x7f
        mov a, @r0
        cjne a, #0xa5, rt51_kernel_failure
        mov r0, #0x7e
rt51_kernel_scan:
        mov a, @r0
        cjne a, #0xa5, rt51_kernel_peak
        dec r0
        cjne r0, #0x2f, rt51_kernel_scan
rt51_kernel_peak:
        mov a, r0
        clr c
        subb a, #0x2f
        mov _rt51_kernel_stack_peak, a
        sjmp rt51_kernel_ok
rt51_kernel_failure:
        ljmp rt51_bad_kernel
rt51_kernel_ok:
        mov a, _rt51_from_len
        jz rt51_load
        mov r1, a
        mov r0, #0x80
        mov dpl, _rt51_from
        mov dph, (_rt51_from + 1)
rt51_save_loop:
        mov a, @r0
        movx @dptr, a
        inc r0
        inc dptr
        djnz r1, rt51_save_loop
rt51_load:
        mov a, _rt51_to_len
        jz rt51_same
        mov r1, a
        add a, #0x7f
        mov _rt51_saved_sp, a
        mov r0, #0x80
        mov dpl, _rt51_to
        mov dph, (_rt51_to + 1)
rt51_load_loop:
        movx a, @dptr
        mov @r0, a
        inc r0
        inc dptr
        djnz r1, rt51_load_loop
rt51_same:
        mov sp, _rt51_saved_sp
        pop _rt51_compiler_bits
        pop _bp
        pop 0x07
        pop 0x06
        pop 0x05
        pop 0x04
        pop 0x03
        pop 0x02
        pop 0x01
        pop 0x00
        ; CJNE 会改变 CY，必须先判定返回类型，再恢复任务 PSW。
        mov a, _rt51_port_kind
        jz rt51_return
        cjne a, #1, rt51_return
        pop psw
        pop dph
        pop dpl
        pop b
        pop acc
        pop _rt51_ie
        reti
rt51_return:
        pop psw
        pop dph
        pop dpl
        pop b
        pop acc
        pop _rt51_ie
        ret
rt51_bad_kernel:
        mov _rt51_fault, #3
rt51_fatal:
        clr _rt51_ea
        sjmp rt51_fatal

        ; SDCC 的自动 bit 寄存器是所有模块共用的 OVR 字节，和用户全局 bit 分开。
        .area BIT_BANK (REL,OVR,DATA)
_rt51_compiler_bits:
        .ds 1
        .area CSEG (CODE)
    __endasm;
}
