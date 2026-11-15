;--------------------------------------------------------
; File Created by SDCC : free open source ISO C Compiler
; Version 4.6.0 #0 (MINGW64)
;--------------------------------------------------------
	.module main
	
	.optsdcc -mmcs251 --model-small
;--------------------------------------------------------
; Public variables in this module
;--------------------------------------------------------
	.globl _copy_source
	.globl _main
	.globl _initialized_word
	.globl _initialized_bytes
	.globl _copy_target
	.globl _bss_bytes
	.globl _data_bss
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
_data_bss::
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
_bss_bytes::
	.ds 9
_copy_target::
	.ds 4
;--------------------------------------------------------
; absolute external ram data
;--------------------------------------------------------
	.area XABS    (ABS,XDATA)
;--------------------------------------------------------
; initialized external ram data
;--------------------------------------------------------
	.area XISEG   (XDATA)
_initialized_bytes::
	.ds 5
_initialized_word::
	.ds 4
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
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
	ejmp	__sdcc_mcs251_unhandled_interrupt
	.ds	4
__sdcc_mcs251_reset_trampoline::
	ejmp	__sdcc_gsinit_startup
__sdcc_mcs251_unhandled_interrupt::
	clr	0xaf
__sdcc_mcs251_unhandled_halt::
	sjmp	__sdcc_mcs251_unhandled_halt
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
; mx251-function begin schema=1 symbol=_main isr=0 addressTaken=0 opaque=0 indirect=0
; wide capacity function=main value-limit=128 node-limit=512 storage-values=512 storage-nodes=2048
; mcs251 wide-mir fallback: main
; wide rejection: node=5 icode=5 opcode=380 values=3 objects=0 frame=0
; wide refusal function=main stage=import reason=assignment-cast-or-memory-contract source-line=529 icode=5 opcode=380 values=3 nodes=5 value-limit=128 node-limit=512 objects=0 frame=0
;------------------------------------------------------------
;Allocation info for local variables in function 'main'
;------------------------------------------------------------
;index         Allocated to registers 
;------------------------------------------------------------
;	src/main.c:17: void main(void)
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
;	src/main.c:20: observed = initialized_bytes[0] ^ (unsigned char) initialized_word ^ bss_bytes[0] ^ data_bss;
	mov	dptr,#_initialized_bytes
	mov	dpxl,#(_initialized_bytes >> 16)
; mcs251 single address: no DPX backup
	mov	a,@dpx
	mov	r7,a
	mov	dptr,#(_initialized_word + 3)
	mov	dpxl,#((_initialized_word + 3) >> 16)
	mov	a,@dpx
	xrl	ar7,a
	mov	dptr,#_bss_bytes
	mov	dpxl,#(_bss_bytes >> 16)
; mcs251 single address: no DPX backup
	mov	a,@dpx
	xrl	a,r7
	xrl	a,_data_bss
	mov	_observed,a
;	src/main.c:21: for (index = 0; index < 4; ++index)
	mov	r7,#0x00
00105$:
;	src/main.c:23: copy_target[index] = copy_source[index];
	mov	a,r7
	add	a,#_copy_target
	mov	r6,a
	clr	a
	addc	a,#(_copy_target >> 8)
	mov	r5,a
	clr	a
	addc	a,#(_copy_target >> 16)
	mov	r4,a
;	mcs251 indexed CODE address 8-bit
	movz	wr18,r7
	add	wr18,#_copy_source
	clr	a
	addc	a,#(_copy_source >> 16)
	movz	wr16,a
	mov	dpx,dr16
; mcs251 single address: no DPX backup
	clr	a
	movc	a,@a+dptr
	mov	r3,a
	mov	dpl,r6
	mov	dph,r5
	mov	dpxl,r4
; mcs251 single address: no DPX backup
	mov	a,r3
	mov	@dpx,a
;	src/main.c:21: for (index = 0; index < 4; ++index)
	inc	r7
	cjne	r7,#0x04,00124$
00124$:
	jc	00105$
;	src/main.c:25: while (1) { }
00103$:
	sjmp	00103$
;	src/main.c:26: }
	eret
; mx251-function end schema=1 symbol=_main
	.area CSEG    (CODE)
	.area CONST   (CODE)
	.area CONST   (CODE)
_copy_source:
	.db #0x10	; 16
	.db #0x32	; 50	'2'
	.db #0x54	; 84	'T'
	.db #0x76	; 118	'v'
	.area CSEG    (CODE)
	.area XINIT   (CODE)
__xinit__initialized_bytes:
	.db #0x41	; 65	'A'
	.db #0x02	; 2
	.db #0x83	; 131
	.db #0xa4	; 164
	.db #0x15	; 21
__xinit__initialized_word:
	.byte #0x89, #0xab, #0xcd, #0xef	; 2309737967
	.area CABS    (ABS,CODE)
