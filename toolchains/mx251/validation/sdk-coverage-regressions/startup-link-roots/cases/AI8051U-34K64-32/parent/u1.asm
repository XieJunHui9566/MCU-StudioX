;--------------------------------------------------------
; File Created by SDCC : free open source ISO C Compiler
; Version 4.6.0 #0 (MINGW64)
;--------------------------------------------------------
	.module remote
	
	.optsdcc -mmcs251 --model-small
;--------------------------------------------------------
; Public variables in this module
;--------------------------------------------------------
	.globl _Start
	.globl _start
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
;--------------------------------------------------------
; overlayable items in internal ram
;--------------------------------------------------------
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
; global & static initialisations
;--------------------------------------------------------
	.area HOME    (CODE)
	.area GSINIT  (CODE)
	.area GSFINAL (CODE)
	.area GSINIT  (CODE)
;--------------------------------------------------------
; Home
;--------------------------------------------------------
	.area HOME    (CODE)
	.area HOME    (CODE)
;--------------------------------------------------------
; code
;--------------------------------------------------------
	.area CSEG    (CODE)
; wide capacity function=Start value-limit=128 node-limit=512 storage-values=512 storage-nodes=2048
; wide imported function=Start values=1 nodes=6 objects=0 frame=0
; wide candidate-cost function=Start id=0 heuristic=2 spills=0 base=1 original=1
; wide candidate function=Start id=0 success=1 instructions=2 bytes=4 unknown=0 frame=0 text-fnv=37870c33 instruction-winner=0 selected=0
; wide allocation selected=0 candidates=1 heuristic=2 instructions=2 frame=0
; wide cost objective=weighted-instructions known-bytes=4 unknown=0 group-known=1

; mcs251 wide-mir accepted: Start; values=1 nodes=6 frame=0 folded-casts=0
_Start:
; wide v0 observed width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000000
; wide ordinary ABI2 entry: first=DPL/DPH/B/A stack=physical
__wm__Start_2:
; wide symbol store _observed width=1 volatile=1
	mov	(_observed+0),#0x64
__wm__Start_1:
	eret
; mx251-effects schema=1 function=_Start abi=2 complete=0 reads=ffffffff writes=ffffffff specialReads=ffffffff specialWrites=ffffffff flagsReads=ffffffff flagsWrites=ffffffff memory=3 instructions=2 unknown=1 calls=0
; wide capacity function=start value-limit=128 node-limit=512 storage-values=512 storage-nodes=2048
; wide imported function=start values=2 nodes=7 objects=0 frame=0
; wide candidate-cost function=start id=0 heuristic=5 spills=0 base=1 original=1
; wide candidate function=start id=0 success=1 instructions=5 bytes=12 unknown=0 frame=0 text-fnv=c26ce3bc instruction-winner=0 selected=0
; wide allocation selected=0 candidates=1 heuristic=5 instructions=5 frame=0
; wide cost objective=weighted-instructions known-bytes=12 unknown=0 group-known=1

; mcs251 wide-mir accepted: start; values=2 nodes=7 frame=0 folded-casts=0
_start:
; wide v0 iTemp0 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000000
; wide v1 iTemp2 width=1 reg=7 slot=-1 edges=0000000000000000:0000000000000000
; wide ordinary ABI2 entry: first=DPL/DPH/B/A stack=physical
__wm__start_2:
	mov	r9,dpl
	mov	r7,r9
; wide byte bitwise op=xrl
	xrl	r7,#0x53
; wide ordinary ABI2 return physical=1
	mov	dpl,r7
	eret
__wm__start_1:
; mx251-effects schema=1 function=_start abi=2 complete=1 reads=00000280 writes=00000280 specialReads=00000005 specialWrites=00000005 flagsReads=ffffffff flagsWrites=ffffffff memory=1 instructions=5 unknown=0 calls=0
	.area CSEG    (CODE)
	.area CONST   (CODE)
	.area XINIT   (CODE)
	.area CABS    (ABS,CODE)
