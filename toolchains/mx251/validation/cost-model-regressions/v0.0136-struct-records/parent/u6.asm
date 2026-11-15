;--------------------------------------------------------
; File Created by SDCC : free open source ISO C Compiler
; Version 4.6.0 #0 (MINGW64)
;--------------------------------------------------------
	.module struct_records
	
	.optsdcc -mmcs251 --model-small
;--------------------------------------------------------
; Public variables in this module
;--------------------------------------------------------
	.globl ___m251r4_struct_records
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
; wide recolor summary function=struct_records attempts=15 emitted=0 improvements=0 old-bytes=442 bytes=442 old-weighted=668 weighted=668 old-frame=40 frame=40 old-spills=3 spills=3
; wide candidate-cost function=struct_records id=0 heuristic=707 spills=4 base=4 original=4
; wide candidate function=struct_records id=0 success=1 instructions=163 bytes=445 unknown=0 frame=39 text-fnv=bed04907 instruction-winner=2 selected=2
; wide candidate-cost function=struct_records id=1 heuristic=707 spills=4 base=4 original=4
; wide candidate function=struct_records id=1 success=1 instructions=163 bytes=445 unknown=0 frame=39 text-fnv=bed04907 instruction-winner=2 selected=2
; wide candidate-cost function=struct_records id=2 heuristic=668 spills=3 base=4 original=4
; wide candidate function=struct_records id=2 success=1 instructions=156 bytes=442 unknown=0 frame=40 text-fnv=77167d58 instruction-winner=2 selected=2
; wide candidate-cost function=struct_records id=3 heuristic=673 spills=3 base=4 original=4
; wide candidate function=struct_records id=3 success=1 instructions=157 bytes=444 unknown=0 frame=40 text-fnv=c79b9e4c instruction-winner=2 selected=2
; wide allocation selected=2 candidates=4 heuristic=668 instructions=156 frame=40
; wide cost objective=weighted-instructions known-bytes=442 unknown=0 group-known=1

; mcs251 wide-mir accepted: struct_records; values=63 nodes=94 frame=40 folded-casts=6
___m251r4_struct_records:
; wide v0 iTemp0 width=4 reg=4 slot=-1 edges=0000000000000000:00000000000657fe
; wide v1 iTemp1 width=4 reg=12 slot=-1 edges=0000000000000000:0000000000000005
; wide v2 iTemp4 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000003
; wide v3 iTemp5 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000001
; wide v4 iTemp2 width=1 reg=1 slot=-1 edges=0000000000000000:0000b7dffffe57e1
; wide v5 iTemp8 width=4 reg=-1 slot=28 edges=0000000000000000:6fd7b7dffffe57d1
; wide v6 iTemp11 width=4 reg=-1 slot=32 edges=0000000000000000:6fd7b7dffffe57b1
; wide v7 iTemp37 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000065771
; wide v8 iTemp13 width=2 reg=14 slot=-1 edges=0000000000000000:00000000000000f1
; wide v9 iTemp14 width=4 reg=12 slot=-1 edges=0000000000000000:00000000000254f1
; wide v10 iTemp15 width=4 reg=24 slot=-1 edges=0000000000000000:00000000000002f1
; wide v11 iTemp21 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v12 iTemp120 width=2 reg=24 slot=-1 edges=0000000000000000:00000000000002f1
; wide v13 iTemp121 width=2 reg=6 slot=-1 edges=0000000000000000:0000000000000000
; wide v14 iTemp26 width=2 reg=24 slot=-1 edges=0000000000000000:00000000000002f1
; wide v15 iTemp32 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v16 iTemp34 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v17 iTemp36 width=4 reg=24 slot=-1 edges=0000000000000000:00000000000002f1
; wide v18 iTemp39 width=1 reg=8 slot=-1 edges=0000000000000000:00000000000000f1
; wide v19 iTemp56 width=4 reg=-1 slot=36 edges=0000000000000000:0000b7dffff00070
; wide v20 iTemp89 width=1 reg=8 slot=-1 edges=0000000000000000:0000b7dfffe80070
; wide v21 iTemp42 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000180070
; wide v22 iTemp46 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000180070
; wide v23 iTemp124 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000180070
; wide v24 iTemp122 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000180070
; wide v25 iTemp123 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000180070
; wide v26 iTemp48 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000180070
; wide v27 iTemp49 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000180070
; wide v28 iTemp43 width=4 reg=4 slot=-1 edges=0000000000000000:000000dfe0180070
; wide v29 iTemp52 width=4 reg=28 slot=-1 edges=0000000000000000:000017dfd0180070
; wide v30 iTemp55 width=1 reg=9 slot=-1 edges=0000000000000000:00000001b0180070
; wide v31 iTemp58 width=4 reg=12 slot=-1 edges=0000000000000000:0000000f70180070
; wide v32 iTemp59 width=1 reg=3 slot=-1 edges=0000000000000000:00000000f0180070
; wide v33 iTemp60 width=1 reg=9 slot=-1 edges=0000000000000000:00000000b0180070
; wide v34 iTemp62 width=4 reg=24 slot=-1 edges=0000000000000000:000000d8b0180070
; wide v35 iTemp65 width=2 reg=2 slot=-1 edges=0000000000000000:00000014b0180070
; wide v36 iTemp69 width=1 reg=9 slot=-1 edges=0000000000000000:0000000c30180070
; wide v37 iTemp70 width=2 reg=6 slot=-1 edges=0000000000000000:0000000000000000
; wide v38 iTemp71 width=2 reg=14 slot=-1 edges=0000000000000000:0000000430180070
; wide v39 iTemp72 width=2 reg=14 slot=-1 edges=0000000000000000:0000070430180070
; wide v40 iTemp74 width=4 reg=4 slot=-1 edges=0000000000000000:0000b68020180070
; wide v41 iTemp77 width=4 reg=24 slot=-1 edges=0000000000000000:0000018020180070
; wide v42 iTemp78 width=4 reg=24 slot=-1 edges=0000000000000000:0000018020180070
; wide v43 iTemp82 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v44 iTemp83 width=4 reg=12 slot=-1 edges=0000000000000000:0000210020180070
; wide v45 iTemp86 width=1 reg=9 slot=-1 edges=0000000000000000:0000110000180070
; wide v46 iTemp87 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v47 iTemp88 width=4 reg=12 slot=-1 edges=0000000000000000:0000010000180070
; wide v48 iTemp116 width=1 reg=8 slot=-1 edges=0000000000000000:6fd6000000000060
; wide v49 iTemp92 width=2 reg=6 slot=-1 edges=0000000000000000:0001000000000060
; wide v50 iTemp93 width=4 reg=4 slot=-1 edges=0000000000000000:03d1000000000060
; wide v51 iTemp94 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v52 iTemp95 width=4 reg=24 slot=-1 edges=0000000000000000:01c5000000000060
; wide v53 iTemp100 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v54 iTemp101 width=2 reg=14 slot=-1 edges=0000000000000000:0015000000000060
; wide v55 iTemp102 width=4 reg=12 slot=-1 edges=0000000000000000:0015000000000060
; wide v56 iTemp103 width=4 reg=12 slot=-1 edges=0000000000000000:0015000000000060
; wide v57 iTemp104 width=4 reg=12 slot=-1 edges=0000000000000000:0c05000000000060
; wide v58 iTemp109 width=4 reg=4 slot=-1 edges=0000000000000000:0201000000000060
; wide v59 iTemp110 width=1 reg=9 slot=-1 edges=0000000000000000:0201000000000060
; wide v60 iTemp112 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v61 iTemp113 width=4 reg=4 slot=-1 edges=0000000000000000:0001000000000060
; wide v62 iTemp118 width=1 reg=9 slot=-1 edges=0000000000000000:0001000000000060
; wide object records slot=0 size=28
	add	spx,#40
__wm____m251r4_struct_records_12:
	mov	dr20,#0x0040
	cmp	dr12,dr20
; wide short branch direction=forward bound=12 kind=conditional
	jle	__wm____m251r4_struct_records_13
; wide byte copy
	mov	r9,#0x40
; wide short branch direction=forward bound=6 kind=goto
	sjmp	__wm____m251r4_struct_records_14
__wm____m251r4_struct_records_13:
	mov	r9,r15
__wm____m251r4_struct_records_14:
; wide byte copy
	mov	r1,r9
	mov	dr16,dr4
	mov	@spx-0x000b,wr16
	mov	@spx-0x0009,wr18
	mov	dr16,spx
	sub	dr16,#39
	anl	wr16,#0x00ff
	mov	@spx-0x0007,wr16
	mov	@spx-0x0005,wr18
; wide byte copy
	mov	r9,#0x00
__wm____m251r4_struct_records_61:
__wm____m251r4_struct_records_4:
	movz	wr16,r9
	mov	wr20,#0x0007
; wide unsigned multiply 8x8 -> 16
	mul	wr16,wr20
	mov	wr14,wr18
	mov	wr22,wr14
	sub	wr20,wr20
	mov	wr12,@spx-0x0007
	mov	wr14,@spx-0x0005
	add	dr12,dr20
	anl	wr12,#0x00ff
	mov	dr24,dr12
; wide memory store width=1 volatile-bytes=0
	mov	@dr24,r9
	mov	wr24,wr6
	movz	wr20,r9
	add	wr24,wr20
; wide indexed address op=+ index-width=4
	mov	dr16,dr12
	add	dr16,#0x0001
	anl	wr16,#0x00ff
; wide memory store width=2 volatile-bytes=0
	mov	@dr16,wr24
	mov	dr24,dr4
	movz	wr22,r9
	sub	wr20,wr20
	add	dr24,dr20
; wide indexed address op=+ index-width=4
	mov	dr16,dr12
	add	dr16,#0x0003
	anl	wr16,#0x00ff
; wide memory store width=4 volatile-bytes=0
	mov	@dr16,wr24
	mov	@dr16+2,wr26
	inc	r9
; wide byte compare signed=0
	cmp	r9,#0x04
	jnc	__wm____m251r4_struct_records_1000001
	ejmp	__wm____m251r4_struct_records_4
__wm____m251r4_struct_records_1000001:
	mov	a,#((_inputs+0)>>16)
	movz	wr16,a
	mov	wr18,#(_inputs+0)
	mov	@spx-0x0003,wr16
	mov	@spx-0x0001,wr18
; wide byte copy
	mov	r8,#0x00
__wm____m251r4_struct_records_64:
__wm____m251r4_struct_records_7:
; wide byte compare signed=0
	cmp	r8,r1
	jc	__wm____m251r4_struct_records_1000002
	ejmp	__wm____m251r4_struct_records_2
__wm____m251r4_struct_records_1000002:
; wide byte copy
	mov	r9,r8
; wide byte copy
; wide byte bitwise op=anl
	anl	r9,#0x03
	mov	wr16,#0x0007
	movz	wr20,r9
; wide unsigned multiply 8x8 -> 8
	mul	wr16,wr20
	mov	wr10,wr18
	mov	r9,r11
; wide byte copy
	mov	wr4,@spx-0x0007
	mov	wr6,@spx-0x0005
	movz	wr22,r9
	sub	wr20,wr20
	add	dr4,dr20
	anl	wr4,#0x00ff
	mov	dr28,dr4
; wide memory load width=1 volatile-bytes=0
	mov	r9,@dr28
	mov	wr12,@spx-0x0003
	mov	wr14,@spx-0x0001
	movz	wr22,r8
	sub	wr20,wr20
	add	dr12,dr20
	anl	wr12,#0x00ff
; wide memory load width=1 volatile-bytes=0
	mov	r3,@dr12
; wide byte bitwise op=xrl
	xrl	r9,r3
; wide memory store width=1 volatile-bytes=0
	mov	@dr28,r9
	mov	dr24,dr4
	add	dr24,#0x0001
	anl	wr24,#0x00ff
; wide memory load width=2 volatile-bytes=0
	mov	wr2,@dr24
; wide memory load width=1 volatile-bytes=0
	mov	r9,@dr12
	mov	wr14,wr2
	movz	wr20,r9
	add	wr14,wr20
	add	wr14,#0x0001
; wide memory store width=2 volatile-bytes=0
	mov	@dr24,wr14
	add	dr4,#0x0003
	anl	wr4,#0x00ff
; wide memory load width=4 volatile-bytes=0
	mov	wr24,@dr4
	mov	wr26,@dr4+2
	add	dr24,dr24
	mov	wr22,wr14
	sub	wr20,wr20
	mov	dr12,dr24
	xrl	wr12,wr20
	xrl	wr14,wr22
; wide memory load width=1 volatile-bytes=0
	mov	r9,@dr28
	movz	wr22,r9
	sub	wr20,wr20
	xrl	wr12,wr20
	xrl	wr14,wr22
; wide memory store width=4 volatile-bytes=0
	mov	@dr4,wr12
	mov	@dr4+2,wr14
	inc	r8
	ejmp	__wm____m251r4_struct_records_7
__wm____m251r4_struct_records_2:
; wide byte copy
	mov	r8,#0x00
__wm____m251r4_struct_records_65:
__wm____m251r4_struct_records_9:
	movz	wr16,r8
	mov	wr20,#0x0007
; wide unsigned multiply 8x8 -> 16
	mul	wr16,wr20
	mov	wr6,wr18
	mov	wr22,wr6
	sub	wr20,wr20
	mov	wr4,@spx-0x0007
	mov	wr6,@spx-0x0005
	add	dr4,dr20
	anl	wr4,#0x00ff
; wide indexed address op=+ index-width=4
	mov	dr16,dr4
	add	dr16,#0x0003
	anl	wr16,#0x00ff
; wide memory load width=4 volatile-bytes=0
	mov	wr24,@dr16
	mov	wr26,@dr16+2
; wide indexed address op=+ index-width=4
	mov	dr16,dr4
	add	dr16,#0x0001
	anl	wr16,#0x00ff
; wide memory load width=2 volatile-bytes=0
	mov	wr14,@dr16
	sub	wr12,wr12
	mov	r12,r13
	mov	r13,r14
	mov	r14,r15
	mov	r15,#0
	add	dr12,dr24
; wide memory load width=1 volatile-bytes=0
	mov	r9,@dr4
	mov	dr4,dr12
	movz	wr22,r9
	sub	wr20,wr20
	add	dr4,dr20
	mov	wr16,@spx-0x000b
	mov	wr18,@spx-0x0009
	xrl	wr16,wr4
	xrl	wr18,wr6
	mov	@spx-0x000b,wr16
	mov	@spx-0x0009,wr18
	inc	r8
; wide byte compare signed=0
	cmp	r8,#0x04
	jnc	__wm____m251r4_struct_records_1000003
	ejmp	__wm____m251r4_struct_records_9
__wm____m251r4_struct_records_1000003:
	mov	wr4,@spx-0x000b
	mov	wr6,@spx-0x0009
	sub	spx,#40
	eret
__wm____m251r4_struct_records_11:
; mx251-effects schema=1 function=___m251r4_struct_records abi=4 complete=0 reads=ffffffff writes=ffffffff specialReads=ffffffff specialWrites=ffffffff flagsReads=ffffffff flagsWrites=ffffffff memory=3 instructions=156 unknown=23 calls=0
	.area CSEG    (CODE)
	.area CONST   (CODE)
	.area XINIT   (CODE)
	.area CABS    (ABS,CODE)
