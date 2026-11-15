;--------------------------------------------------------
; File Created by SDCC : free open source ISO C Compiler
; Version 4.6.0 #0 (MINGW64)
;--------------------------------------------------------
	.module probe
	
	.optsdcc -mmcs251 --model-large
;--------------------------------------------------------
; Public variables in this module
;--------------------------------------------------------
	.globl ___m251r4_f
	.globl _observer
	.globl _io
;--------------------------------------------------------
; special function registers
;--------------------------------------------------------
	.area RSEG    (ABS,DATA)
	.org 0x0000
_io	=	0x00b1
_observer	=	0x00f0
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
; wide capacity function=f value-limit=128 node-limit=512 storage-values=512 storage-nodes=2048
; wide imported function=f values=13 nodes=21 objects=0 frame=0
; wide loop-cost kind=native-indexed-two-use function=f decision=1 legacy-fallback=0
; wide indexed-field flags-known=1 emitted-sites=2
; wide native-indexed-two-use choice function=f groups=1 selected=1 original-result=1 trial-result=1 old-bytes=57 new-bytes=46 old-unknown=0 new-unknown=0 old-weighted=21 new-weighted=16 old-frame=0 new-frame=0 old-spills=0 new-spills=0
; wide pointer-copy two-use-old ; wide loop-cost kind=native-indexed-field function=f decision=1 legacy-fallback=0
; wide pointer-copy two-use-old ; wide indexed-field flags-known=1 emitted-sites=1
; wide pointer-copy two-use-old ; wide native-indexed-field choice function=f sites=1 selected=1 original-result=1 trial-result=1 old-bytes=66 new-bytes=57 old-unknown=0 new-unknown=0 old-weighted=24 new-weighted=21 old-frame=0 new-frame=0 old-spills=0 new-spills=0
; wide pointer-copy two-use-old ; wide pointer-copy indexed-old ; wide candidate-cost function=f id=0 heuristic=24 spills=0 base=1 original=1
; wide pointer-copy two-use-old ; wide pointer-copy indexed-old ; wide candidate function=f id=0 success=1 instructions=24 bytes=66 unknown=0 frame=0 text-fnv=f6768331 instruction-winner=0 selected=0
; wide pointer-copy two-use-old ; wide pointer-copy indexed-old ; wide allocation selected=0 candidates=1 heuristic=24 instructions=24 frame=0
; wide pointer-copy two-use-old ; wide pointer-copy indexed-old ; wide cost objective=weighted-instructions known-bytes=66 unknown=0 group-known=1
; wide pointer-copy two-use-old ; wide candidate-cost function=f id=0 heuristic=21 spills=0 base=1 original=1
; wide pointer-copy two-use-old ; wide candidate function=f id=0 success=1 instructions=21 bytes=57 unknown=0 frame=0 text-fnv=370634cb instruction-winner=0 selected=0
; wide pointer-copy two-use-old ; wide allocation selected=0 candidates=1 heuristic=21 instructions=21 frame=0
; wide pointer-copy two-use-old ; wide cost objective=weighted-instructions known-bytes=57 unknown=0 group-known=1
; wide loop-cost kind=native-indexed-field function=f decision=1 legacy-fallback=0
; wide indexed-field flags-known=1 emitted-sites=2
; wide native-indexed-field choice function=f sites=1 selected=1 original-result=1 trial-result=1 old-bytes=55 new-bytes=46 old-unknown=0 new-unknown=0 old-weighted=19 new-weighted=16 old-frame=0 new-frame=0 old-spills=0 new-spills=0
; wide pointer-copy indexed-old ; wide candidate-cost function=f id=0 heuristic=19 spills=0 base=1 original=1
; wide pointer-copy indexed-old ; wide candidate function=f id=0 success=1 instructions=19 bytes=55 unknown=0 frame=0 text-fnv=d98ffad2 instruction-winner=0 selected=0
; wide pointer-copy indexed-old ; wide allocation selected=0 candidates=1 heuristic=19 instructions=19 frame=0
; wide pointer-copy indexed-old ; wide cost objective=weighted-instructions known-bytes=55 unknown=0 group-known=1
; wide candidate-cost function=f id=0 heuristic=16 spills=0 base=1 original=1
; wide candidate function=f id=0 success=1 instructions=16 bytes=46 unknown=0 frame=0 text-fnv=29c5533e instruction-winner=0 selected=0
; wide allocation selected=0 candidates=1 heuristic=16 instructions=16 frame=0
; wide cost objective=weighted-instructions known-bytes=46 unknown=0 group-known=1

; mcs251 wide-mir accepted: f; values=13 nodes=21 frame=0 folded-casts=0
___m251r4_f:
; wide v0 iTemp1 width=4 reg=12 slot=-1 edges=0000000000000000:00000000000000fc
; wide v1 iTemp3 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v2 iTemp4 width=2 reg=6 slot=-1 edges=0000000000000000:0000000000000001
; wide v3 iTemp5 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000001
; wide v4 io width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000001
; wide v5 iTemp8 width=2 reg=6 slot=-1 edges=0000000000000000:0000000000000001
; wide v6 iTemp9 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000001
; wide v7 io width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000001
; wide v8 iTemp11 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v9 iTemp12 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000000
; wide v10 iTemp13 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000000
; wide v11 io width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000000
; wide v12 observer width=1 reg=7 slot=-1 edges=0000000000000000:0000000000000000
__wm____m251r4_f_2:
	anl	wr12,#0x00ff
; wide native-indexed-two-use group=5 part=1 key=6 ordinal=2 extent=7 offset=4 base=12
; wide native-indexed-field flags=3 key=6 ordinal=2 extent=7 offset=4 base=12
	mov	wr6,@dr12+4
; wide getbyte offset=1 width=2
; wide getbyte path=byte reg=6
	mov	r9,r6
; wide byte copy
; wide symbol store _io width=1 volatile=1
	mov	(_io+0),r9
; wide native-indexed-two-use group=5 part=2 key=11 ordinal=2 extent=7 offset=4 base=12
	mov	wr6,@dr12+4
	mov	r9,r7
; wide byte copy
; wide symbol store _io width=1 volatile=1
	mov	(_io+0),r9
; wide native-indexed-field flags=15 key=16 ordinal=2 extent=7 offset=6 base=12
	mov	r9,@dr12+6
; wide byte compare signed=0
	cmp	r9,#0x07
	je	__wm____m251r4_f_1000000
; wide byte boolean
	mov	r9,#0
	sjmp	__wm____m251r4_f_1000001
__wm____m251r4_f_1000000:
	mov	r9,#1
__wm____m251r4_f_1000001:
; wide byte copy
; wide symbol store _io width=1 volatile=1
	mov	(_io+0),r9
; wide symbol load _observer width=1 volatile=1
	mov	r7,(_observer+0)
	eret
__wm____m251r4_f_1:
; mx251-effects schema=1 function=___m251r4_f abi=4 complete=0 reads=ffffffff writes=ffffffff specialReads=ffffffff specialWrites=ffffffff flagsReads=ffffffff flagsWrites=ffffffff memory=3 instructions=16 unknown=7 calls=0
	.area CSEG    (CODE)
	.area CONST   (CODE)
	.area XINIT   (CODE)
	.area CABS    (ABS,CODE)
