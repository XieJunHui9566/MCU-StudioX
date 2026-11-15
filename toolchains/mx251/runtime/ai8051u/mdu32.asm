; SPDX-License-Identifier: GPL-2.0-or-later WITH SDCC-exception
; MX251 V0.0100 -- AI8051U 32-bit MDU runtime.
; ABI4: left DR4, right DR12, result DR4; DR0/DR4/A and flags are clobbered.
; No static data, shared scratch, interrupt masking or changed register bank.
; AI8051U manual sections 36.1-36.3: only MOV DMAIR,#immediate triggers MDU.
; Runtime direct-call zero/overflow behavior is bounded; C /0 and MIN/-1
; remain undefined and must not be used as ordinary C correctness inputs.

.module mx251_ai8051u_mdu32
.globl ___m251r4__mullong
.globl ___m251r4__divulong
.globl ___m251r4__modulong
.globl ___m251r4__divslong
.globl ___m251r4__modslong
.area CSEG (CODE)

___m251r4__mullong:
    mov dr0,dr12
    mov 0xed,#0x02
    eret

___m251r4__divulong:
    cmp dr12,#0
    jne 00101$
    mov dr4,#0xffffffff
    eret
00101$:
    mov dr0,dr12
    mov 0xed,#0x04
    eret

___m251r4__modulong:
    cmp dr12,#0
    je 00201$
    mov dr0,dr12
    mov 0xed,#0x04
    mov dr4,dr0
00201$:
    eret

___m251r4__divslong:
    cmp dr12,#0
    jne 00301$
    mov a,r4
    jnb acc.7,00302$
    mov dr4,#1
    eret
00302$:
    mov dr4,#0xffffffff
    eret
00301$:
    cmp dr4,#0x80000000
    jne 00303$
    cmp dr12,#0xffffffff
    je 00304$
00303$:
    mov dr0,dr12
    mov 0xed,#0x06
00304$:
    eret

___m251r4__modslong:
    cmp dr12,#0
    je 00404$
    cmp dr4,#0x80000000
    jne 00403$
    cmp dr12,#0xffffffff
    jne 00403$
    mov dr4,#0
    eret
00403$:
    mov dr0,dr12
    mov 0xed,#0x06
    mov dr4,dr0
00404$:
    eret
