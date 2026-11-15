;--------------------------------------------------------
; File Created by SDCC : free open source ISO C Compiler
; Version 4.6.0 #0 (MINGW64)
;--------------------------------------------------------
	.module main
	
	.optsdcc -mmcs251 --model-small
;--------------------------------------------------------
; Public variables in this module
;--------------------------------------------------------
	.globl _main
	.globl _dead_bridge
	.globl _Start
	.globl _start
	.globl _observed
;--------------------------------------------------------
; special function registers
;--------------------------------------------------------
	.area RSEG    (ABS,DATA)
	.org 0x0000
;--------------------------------------------------------
; special function bits
;--------------------------------------------------------
	.area RSEG    (ABS,DATA)
	.org 0x0000
;--------------------------------------------------------
; overlayable register banks
;--------------------------------------------------------
	.area REG_BANK_0	(REL,OVR,DATA)
	.ds 8
;--------------------------------------------------------
; internal ram data
;--------------------------------------------------------
	.area DSEG    (DATA)
_observed::
	.ds 1
;--------------------------------------------------------
; overlayable items in internal ram
;--------------------------------------------------------
;--------------------------------------------------------
; Stack segment in internal ram
;--------------------------------------------------------
	.area SSEG
__start__stack:
	.ds	1

;--------------------------------------------------------
; indirectly addressable internal ram data
;--------------------------------------------------------
	.area ISEG    (DATA)
;--------------------------------------------------------
; absolute internal ram data
;--------------------------------------------------------
	.area IABS    (ABS,DATA)
	.area IABS    (ABS,DATA)
;--------------------------------------------------------
; bit data
;--------------------------------------------------------
	.area BSEG    (BIT)
;--------------------------------------------------------
; paged external ram data
;--------------------------------------------------------
	.area PSEG    (PAG,XDATA)
;--------------------------------------------------------
; uninitialized external ram data
;--------------------------------------------------------
	.area XSEG    (XDATA)
;--------------------------------------------------------
; absolute external ram data
;--------------------------------------------------------
	.area XABS    (ABS,XDATA)
;--------------------------------------------------------
; initialized external ram data
;--------------------------------------------------------
	.area XISEG   (XDATA)
	.area HOME    (CODE)
	.area GSINIT0 (CODE)
	.area GSINIT1 (CODE)
	.area GSINIT2 (CODE)
	.area GSINIT3 (CODE)
	.area GSINIT4 (CODE)
	.area GSINIT5 (CODE)
	.area GSINIT  (CODE)
	.area GSFINAL (CODE)
	.area CSEG    (CODE)
;--------------------------------------------------------
; interrupt vector
;--------------------------------------------------------
	.area HOME    (CODE)
__interrupt_vect:
	ljmp	__sdcc_mcs251_reset_trampoline
__sdcc_mcs251_reset_trampoline::
	ejmp	__sdcc_gsinit_startup
;--------------------------------------------------------
; global & static initialisations
;--------------------------------------------------------
	.area HOME    (CODE)
	.area GSINIT  (CODE)
	.area GSFINAL (CODE)
	.area GSINIT  (CODE)
	.globl __sdcc_gsinit_startup
	.globl __sdcc_program_startup
	.globl __start__stack
	.globl __mcs51_genXINIT
	.globl __mcs51_genXRAMCLEAR
	.globl __mcs51_genRAMCLEAR
	.area GSFINAL (CODE)
	ejmp	__sdcc_program_startup
;--------------------------------------------------------
; Home
;--------------------------------------------------------
	.area HOME    (CODE)
	.area HOME    (CODE)
__sdcc_program_startup:
	ecall	_main
__sdcc_program_exit:
	sjmp	.
;	return from main will return to caller
;--------------------------------------------------------
; code
;--------------------------------------------------------
	.area CSEG    (CODE)
; wide capacity function=dead_bridge value-limit=128 node-limit=512 storage-values=512 storage-nodes=2048
; wide imported function=dead_bridge values=0 nodes=5 objects=0 frame=0
; wide candidate-cost function=dead_bridge id=0 heuristic=2 spills=0 base=1 original=1
; wide candidate function=dead_bridge id=0 success=1 instructions=2 bytes=5 unknown=0 frame=0 text-fnv=b26f8859 instruction-winner=0 selected=0
; wide allocation selected=0 candidates=1 heuristic=2 instructions=2 frame=0
; wide cost objective=weighted-instructions known-bytes=5 unknown=0 group-known=1

; mcs251 wide-mir accepted: dead_bridge; values=0 nodes=5 frame=0 folded-casts=0
_dead_bridge:
; wide ordinary ABI2 entry: first=DPL/DPH/B/A stack=physical
__wm__dead_bridge_2:
; wide ordinary ABI call: args=0 stack=0
	ecall	_Start
; mx251-call-effects schema=1 caller=_dead_bridge site=2 callee=_Start abi=2 live=00000000 setup=00000000 finish=00000000 complete=1 saved=
__wm__dead_bridge_1:
	eret
; mx251-effects schema=1 function=_dead_bridge abi=2 complete=0 reads=ffffffff writes=ffffffff specialReads=ffffffff specialWrites=ffffffff flagsReads=ffffffff flagsWrites=ffffffff memory=3 instructions=2 unknown=1 calls=1
; wide capacity function=main value-limit=128 node-limit=512 storage-values=512 storage-nodes=2048
; mcs251 wide-mir fallback: main
; wide rejection: node=4 icode=3 opcode=381 values=0 objects=0 frame=0
; wide refusal function=main stage=import reason=call-abi-or-argument-contract source-line=479 icode=3 opcode=381 values=0 nodes=4 value-limit=128 node-limit=512 objects=0 frame=0
;------------------------------------------------------------
;Allocation info for local variables in function 'main'
;------------------------------------------------------------
;	src/main.c:11: void main(void)
;	-----------------------------------------
;	 function main
;	-----------------------------------------
_main:
	ar7 = 0x07
	ar6 = 0x06
	ar5 = 0x05
	ar4 = 0x04
	ar3 = 0x03
	ar2 = 0x02
	ar1 = 0x01
	ar0 = 0x00
;	src/main.c:13: observed = start(observed);
	mov	dpl, _observed
	ecall	_start
	mov	_observed,dpl
;	src/main.c:14: while (1) { }
00102$:
	sjmp	00102$
;	src/main.c:15: }
	eret
	.area CSEG    (CODE)
	.area CONST   (CODE)
	.area XINIT   (CODE)
	.area CABS    (ABS,CODE)
