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
; wide local-pointer-copy choice function=struct_records copies=3 selected=1 original-result=1 trial-result=1 old-bytes=442 new-bytes=408 old-unknown=0 new-unknown=0 old-weighted=668 new-weighted=639 old-frame=40 new-frame=36 old-spills=3 new-spills=2
; wide pointer-copy local-original ; wide recolor summary function=struct_records attempts=15 emitted=0 improvements=0 old-bytes=442 bytes=442 old-weighted=668 weighted=668 old-frame=40 frame=40 old-spills=3 spills=3
; wide pointer-copy local-original ; wide candidate-cost function=struct_records id=0 heuristic=707 spills=4 base=4 original=4
; wide pointer-copy local-original ; wide candidate function=struct_records id=0 success=1 instructions=163 bytes=445 unknown=0 frame=39 text-fnv=41721148 instruction-winner=2 selected=2
; wide pointer-copy local-original ; wide candidate-cost function=struct_records id=1 heuristic=707 spills=4 base=4 original=4
; wide pointer-copy local-original ; wide candidate function=struct_records id=1 success=1 instructions=163 bytes=445 unknown=0 frame=39 text-fnv=41721148 instruction-winner=2 selected=2
; wide pointer-copy local-original ; wide candidate-cost function=struct_records id=2 heuristic=668 spills=3 base=4 original=4
; wide pointer-copy local-original ; wide candidate function=struct_records id=2 success=1 instructions=156 bytes=442 unknown=0 frame=40 text-fnv=8807f217 instruction-winner=2 selected=2
; wide pointer-copy local-original ; wide candidate-cost function=struct_records id=3 heuristic=673 spills=3 base=4 original=4
; wide pointer-copy local-original ; wide candidate function=struct_records id=3 success=1 instructions=157 bytes=444 unknown=0 frame=40 text-fnv=e066bfaf instruction-winner=2 selected=2
; wide pointer-copy local-original ; wide allocation selected=2 candidates=4 heuristic=668 instructions=156 frame=40
; wide pointer-copy local-original ; wide cost objective=weighted-instructions known-bytes=442 unknown=0 group-known=1
; wide local-pointer-copy node=19 source=9 destination=10 uses=1 last=20
; wide local-pointer-copy node=46 source=28 destination=29 uses=3 last=64
; wide local-pointer-copy node=83 source=50 destination=58 uses=1 last=84
; wide recolor function=struct_records value=5 reg=12 mode=move moved=32 search=155 bytes=408 weighted=639 frame=36 spills=2 admissible=1 chosen=1
; wide recolor function=struct_records value=5 reg=24 mode=move moved=19 search=33 bytes=427 weighted=685 frame=36 spills=2 admissible=0 chosen=0
; wide recolor function=struct_records value=5 reg=28 mode=move moved=21 search=115 bytes=414 weighted=654 frame=36 spills=2 admissible=1 chosen=0
; wide recolor function=struct_records value=5 reg=0 mode=move moved=15 search=237 bytes=412 weighted=645 frame=36 spills=2 admissible=1 chosen=0
; wide recolor function=struct_records value=6 reg=12 mode=move moved=32 search=155 bytes=412 weighted=641 frame=36 spills=2 admissible=1 chosen=0
; wide recolor function=struct_records value=6 reg=24 mode=move moved=19 search=33 bytes=433 weighted=692 frame=36 spills=2 admissible=0 chosen=0
; wide recolor function=struct_records value=6 reg=28 mode=move moved=21 search=115 bytes=420 weighted=661 frame=36 spills=2 admissible=1 chosen=0
; wide recolor function=struct_records value=6 reg=0 mode=move moved=15 search=237 bytes=416 weighted=647 frame=36 spills=2 admissible=1 chosen=0
; wide recolor function=struct_records value=19 reg=4 mode=move moved=9 search=41 bytes=430 weighted=662 frame=36 spills=2 admissible=1 chosen=0
; wide recolor function=struct_records value=19 reg=12 mode=move moved=21 search=140 bytes=428 weighted=661 frame=36 spills=2 admissible=1 chosen=0
; wide recolor function=struct_records value=19 reg=24 mode=move moved=9 search=19 bytes=430 weighted=662 frame=36 spills=2 admissible=1 chosen=0
; wide recolor function=struct_records value=19 reg=28 mode=move moved=21 search=115 bytes=432 weighted=671 frame=36 spills=2 admissible=0 chosen=0
; wide recolor function=struct_records value=19 reg=0 mode=move moved=15 search=230 bytes=430 weighted=662 frame=36 spills=2 admissible=1 chosen=0
; wide recolor summary function=struct_records attempts=15 emitted=13 improvements=1 old-bytes=442 bytes=408 old-weighted=668 weighted=639 old-frame=40 frame=36 old-spills=3 spills=2
; wide candidate-cost function=struct_records id=0 heuristic=754 spills=9 base=4 original=4
; wide candidate function=struct_records id=0 success=1 instructions=170 bytes=473 unknown=0 frame=47 text-fnv=c49b62cc instruction-winner=2 selected=2
; wide candidate-cost function=struct_records id=1 heuristic=754 spills=9 base=4 original=4
; wide candidate function=struct_records id=1 success=1 instructions=170 bytes=473 unknown=0 frame=36 text-fnv=a6c20fc5 instruction-winner=2 selected=2
; wide candidate-cost function=struct_records id=2 heuristic=639 spills=2 base=4 original=4
; wide candidate function=struct_records id=2 success=1 instructions=147 bytes=408 unknown=0 frame=36 text-fnv=a12e93b8 instruction-winner=2 selected=2
; wide candidate-cost function=struct_records id=3 heuristic=668 spills=3 base=4 original=4
; wide candidate function=struct_records id=3 success=1 instructions=156 bytes=442 unknown=0 frame=40 text-fnv=51c392c9 instruction-winner=2 selected=2
; wide allocation selected=2 candidates=4 heuristic=639 instructions=147 frame=36
; wide cost objective=weighted-instructions known-bytes=408 unknown=0 group-known=1

; mcs251 wide-mir accepted: struct_records; values=63 nodes=94 frame=36 folded-casts=6
___m251r4_struct_records:
; wide v0 iTemp0 width=4 reg=4 slot=-1 edges=0000000000000000:00000000000653fe
; wide v1 iTemp1 width=4 reg=12 slot=-1 edges=0000000000000000:0000000000000005
; wide v2 iTemp4 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000003
; wide v3 iTemp5 width=1 reg=9 slot=-1 edges=0000000000000000:0000000000000001
; wide v4 iTemp2 width=1 reg=9 slot=-1 edges=0000000000000000:0000b7dfdffe53e1
; wide v5 iTemp8 width=4 reg=12 slot=-1 edges=0000000000000000:6bd7b7dfdffe53d1
; wide v6 iTemp11 width=4 reg=-1 slot=28 edges=0000000000000000:6bd7b7dfdffe53b1
; wide v7 iTemp37 width=1 reg=3 slot=-1 edges=0000000000000000:0000000000065371
; wide v8 iTemp13 width=2 reg=24 slot=-1 edges=0000000000000000:00000000000000f1
; wide v9 iTemp14 width=4 reg=24 slot=-1 edges=0000000000000000:00000000000250f1
; wide v10 iTemp15 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v11 iTemp21 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v12 iTemp120 width=2 reg=28 slot=-1 edges=0000000000000000:00000000000002f1
; wide v13 iTemp121 width=2 reg=6 slot=-1 edges=0000000000000000:0000000000000000
; wide v14 iTemp26 width=2 reg=28 slot=-1 edges=0000000000000000:00000000000002f1
; wide v15 iTemp32 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v16 iTemp34 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v17 iTemp36 width=4 reg=28 slot=-1 edges=0000000000000000:00000000000002f1
; wide v18 iTemp39 width=1 reg=8 slot=-1 edges=0000000000000000:00000000000000f1
; wide v19 iTemp56 width=4 reg=-1 slot=32 edges=0000000000000000:0000b7dfdff00070
; wide v20 iTemp89 width=1 reg=8 slot=-1 edges=0000000000000000:0000b7dfdfe80070
; wide v21 iTemp42 width=1 reg=7 slot=-1 edges=0000000000000000:0000000000180070
; wide v22 iTemp46 width=1 reg=7 slot=-1 edges=0000000000000000:0000000000180070
; wide v23 iTemp124 width=1 reg=7 slot=-1 edges=0000000000000000:0000000000180070
; wide v24 iTemp122 width=1 reg=7 slot=-1 edges=0000000000000000:0000000000180070
; wide v25 iTemp123 width=1 reg=7 slot=-1 edges=0000000000000000:0000000000180070
; wide v26 iTemp48 width=1 reg=7 slot=-1 edges=0000000000000000:0000000000180070
; wide v27 iTemp49 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000180070
; wide v28 iTemp43 width=4 reg=28 slot=-1 edges=0000000000000000:000017dfc0180070
; wide v29 iTemp52 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v30 iTemp55 width=1 reg=2 slot=-1 edges=0000000000000000:0000000190180070
; wide v31 iTemp58 width=4 reg=4 slot=-1 edges=0000000000000000:0000000f50180070
; wide v32 iTemp59 width=1 reg=3 slot=-1 edges=0000000000000000:00000000d0180070
; wide v33 iTemp60 width=1 reg=3 slot=-1 edges=0000000000000000:0000000090180070
; wide v34 iTemp62 width=4 reg=24 slot=-1 edges=0000000000000000:000000d890180070
; wide v35 iTemp65 width=2 reg=0 slot=-1 edges=0000000000000000:0000001490180070
; wide v36 iTemp69 width=1 reg=7 slot=-1 edges=0000000000000000:0000000c10180070
; wide v37 iTemp70 width=2 reg=6 slot=-1 edges=0000000000000000:0000000000000000
; wide v38 iTemp71 width=2 reg=6 slot=-1 edges=0000000000000000:0000000410180070
; wide v39 iTemp72 width=2 reg=2 slot=-1 edges=0000000000000000:0000070410180070
; wide v40 iTemp74 width=4 reg=24 slot=-1 edges=0000000000000000:0000b68010180070
; wide v41 iTemp77 width=4 reg=4 slot=-1 edges=0000000000000000:0000018010180070
; wide v42 iTemp78 width=4 reg=4 slot=-1 edges=0000000000000000:0000018010180070
; wide v43 iTemp82 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v44 iTemp83 width=4 reg=4 slot=-1 edges=0000000000000000:0000210010180070
; wide v45 iTemp86 width=1 reg=3 slot=-1 edges=0000000000000000:0000110000180070
; wide v46 iTemp87 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v47 iTemp88 width=4 reg=4 slot=-1 edges=0000000000000000:0000010000180070
; wide v48 iTemp116 width=1 reg=8 slot=-1 edges=0000000000000000:6bd6000000000060
; wide v49 iTemp92 width=2 reg=6 slot=-1 edges=0000000000000000:0001000000000060
; wide v50 iTemp93 width=4 reg=24 slot=-1 edges=0000000000000000:03d1000000000060
; wide v51 iTemp94 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v52 iTemp95 width=4 reg=28 slot=-1 edges=0000000000000000:01c5000000000060
; wide v53 iTemp100 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v54 iTemp101 width=2 reg=6 slot=-1 edges=0000000000000000:0015000000000060
; wide v55 iTemp102 width=4 reg=4 slot=-1 edges=0000000000000000:0015000000000060
; wide v56 iTemp103 width=4 reg=4 slot=-1 edges=0000000000000000:0015000000000060
; wide v57 iTemp104 width=4 reg=4 slot=-1 edges=0000000000000000:0805000000000060
; wide v58 iTemp109 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v59 iTemp110 width=1 reg=9 slot=-1 edges=0000000000000000:0201000000000060
; wide v60 iTemp112 width=4 reg=4 slot=-1 edges=0000000000000000:0000000000000000
; wide v61 iTemp113 width=4 reg=4 slot=-1 edges=0000000000000000:0001000000000060
; wide v62 iTemp118 width=1 reg=9 slot=-1 edges=0000000000000000:0001000000000060
; wide object records slot=0 size=28
; wide node=0 op=385 line=3 depth=0 result=-1 left=-1 right=-1 index=-1 succ=1,-1 in=0000000000000000:0000000000000000 out=0000000000000000:0000000000000000 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=1 op=9 line=3 depth=0 result=-1 left=-1 right=-1 index=-1 succ=2,-1 in=0000000000000000:0000000000000000 out=0000000000000000:0000000000000000 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=2 op=386 line=3 depth=0 result=0 left=-1 right=-1 index=-1 succ=3,-1 in=0000000000000000:0000000000000000 out=0000000000000000:0000000000000001 def=0000000000000000:0000000000000001 use=0000000000000000:0000000000000000
; wide node=3 op=386 line=3 depth=0 result=1 left=-1 right=-1 index=-1 succ=4,-1 in=0000000000000000:0000000000000001 out=0000000000000000:0000000000000003 def=0000000000000000:0000000000000002 use=0000000000000000:0000000000000000
; wide node=4 op=62 line=7 depth=0 result=2 left=1 right=-1 index=-1 succ=5,-1 in=0000000000000000:0000000000000003 out=0000000000000000:0000000000000007 def=0000000000000000:0000000000000004 use=0000000000000000:0000000000000002
; wide node=5 op=362 line=7 depth=0 result=-1 left=2 right=-1 index=-1 succ=6,8 in=0000000000000000:0000000000000007 out=0000000000000000:0000000000000003 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000004
; wide node=6 op=61 line=7 depth=0 result=3 left=-1 right=-1 index=-1 succ=7,-1 in=0000000000000000:0000000000000001 out=0000000000000000:0000000000000009 def=0000000000000000:0000000000000008 use=0000000000000000:0000000000000000
; wide node=7 op=350 line=7 depth=0 result=-1 left=-1 right=-1 index=-1 succ=10,-1 in=0000000000000000:0000000000000009 out=0000000000000000:0000000000000009 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=8 op=385 line=7 depth=0 result=-1 left=-1 right=-1 index=-1 succ=9,-1 in=0000000000000000:0000000000000003 out=0000000000000000:0000000000000003 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=9 op=380 line=7 depth=0 result=3 left=-1 right=1 index=-1 succ=10,-1 in=0000000000000000:0000000000000003 out=0000000000000000:0000000000000009 def=0000000000000000:0000000000000008 use=0000000000000000:0000000000000002
; wide node=10 op=385 line=7 depth=0 result=-1 left=-1 right=-1 index=-1 succ=11,-1 in=0000000000000000:0000000000000009 out=0000000000000000:0000000000000009 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=11 op=380 line=7 depth=0 result=4 left=-1 right=3 index=-1 succ=12,-1 in=0000000000000000:0000000000000009 out=0000000000000000:0000000000000011 def=0000000000000000:0000000000000010 use=0000000000000000:0000000000000008
; wide node=12 op=61 line=8 depth=0 result=5 left=-1 right=0 index=-1 succ=13,-1 in=0000000000000000:0000000000000011 out=0000000000000000:0000000000000031 def=0000000000000000:0000000000000020 use=0000000000000000:0000000000000001
; wide node=13 op=65538 line=9 depth=0 result=6 left=-1 right=-1 index=-1 succ=14,-1 in=0000000000000000:0000000000000031 out=0000000000000000:0000000000000071 def=0000000000000000:0000000000000040 use=0000000000000000:0000000000000000
; wide node=14 op=61 line=9 depth=0 result=7 left=-1 right=-1 index=-1 succ=15,-1 in=0000000000000000:0000000000000071 out=0000000000000000:00000000000000f1 def=0000000000000000:0000000000000080 use=0000000000000000:0000000000000000
; wide node=15 op=385 line=9 depth=0 result=-1 left=-1 right=-1 index=-1 succ=16,-1 in=0000000000000000:00000000000000f1 out=0000000000000000:00000000000000f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=16 op=385 line=9 depth=1 result=-1 left=-1 right=-1 index=-1 succ=17,-1 in=0000000000000000:00000000000000f1 out=0000000000000000:00000000000000f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=17 op=65539 line=9 depth=1 result=8 left=7 right=-1 index=-1 succ=18,-1 in=0000000000000000:00000000000000f1 out=0000000000000000:00000000000001f1 def=0000000000000000:0000000000000100 use=0000000000000000:0000000000000080
; wide node=18 op=43 line=9 depth=1 result=9 left=6 right=8 index=-1 succ=19,-1 in=0000000000000000:00000000000001f1 out=0000000000000000:00000000000002f1 def=0000000000000000:0000000000000200 use=0000000000000000:0000000000000140
; wide node=19 op=65540 line=9 depth=1 result=-1 left=-1 right=-1 index=-1 succ=20,-1 in=0000000000000000:00000000000002f1 out=0000000000000000:00000000000002f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=20 op=65537 line=9 depth=1 result=-1 left=9 right=7 index=-1 succ=21,-1 in=0000000000000000:00000000000002f1 out=0000000000000000:00000000000002f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000280
; wide node=21 op=65540 line=9 depth=1 result=-1 left=-1 right=-1 index=-1 succ=22,-1 in=0000000000000000:00000000000002f1 out=0000000000000000:00000000000002f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=22 op=380 line=9 depth=1 result=12 left=-1 right=0 index=-1 succ=23,-1 in=0000000000000000:00000000000002f1 out=0000000000000000:00000000000012f1 def=0000000000000000:0000000000001000 use=0000000000000000:0000000000000001
; wide node=23 op=65540 line=9 depth=1 result=-1 left=-1 right=-1 index=-1 succ=24,-1 in=0000000000000000:00000000000012f1 out=0000000000000000:00000000000012f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=24 op=43 line=9 depth=1 result=14 left=12 right=7 index=-1 succ=25,-1 in=0000000000000000:00000000000012f1 out=0000000000000000:00000000000042f1 def=0000000000000000:0000000000004000 use=0000000000000000:0000000000001080
; wide node=25 op=65537 line=9 depth=1 result=-1 left=9 right=14 index=-1 succ=26,-1 in=0000000000000000:00000000000042f1 out=0000000000000000:00000000000002f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000004200
; wide node=26 op=65540 line=9 depth=1 result=-1 left=-1 right=-1 index=-1 succ=27,-1 in=0000000000000000:00000000000002f1 out=0000000000000000:00000000000002f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=27 op=65540 line=9 depth=1 result=-1 left=-1 right=-1 index=-1 succ=28,-1 in=0000000000000000:00000000000002f1 out=0000000000000000:00000000000002f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=28 op=43 line=9 depth=1 result=17 left=0 right=7 index=-1 succ=29,-1 in=0000000000000000:00000000000002f1 out=0000000000000000:00000000000202f1 def=0000000000000000:0000000000020000 use=0000000000000000:0000000000000081
; wide node=29 op=65537 line=9 depth=1 result=-1 left=9 right=17 index=-1 succ=30,-1 in=0000000000000000:00000000000202f1 out=0000000000000000:00000000000000f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000020200
; wide node=30 op=43 line=9 depth=1 result=7 left=7 right=-1 index=-1 succ=31,-1 in=0000000000000000:00000000000000f1 out=0000000000000000:00000000000000f1 def=0000000000000000:0000000000000080 use=0000000000000000:0000000000000080
; wide node=31 op=60 line=9 depth=1 result=18 left=7 right=-1 index=-1 succ=32,-1 in=0000000000000000:00000000000000f1 out=0000000000000000:00000000000400f1 def=0000000000000000:0000000000040000 use=0000000000000000:0000000000000080
; wide node=32 op=362 line=9 depth=1 result=-1 left=18 right=-1 index=-1 succ=33,16 in=0000000000000000:00000000000400f1 out=0000000000000000:00000000000000f1 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000040000
; wide node=33 op=65538 line=10 depth=0 result=19 left=-1 right=-1 index=-1 succ=34,-1 in=0000000000000000:0000000000000070 out=0000000000000000:0000000000080070 def=0000000000000000:0000000000080000 use=0000000000000000:0000000000000000
; wide node=34 op=61 line=10 depth=0 result=20 left=-1 right=-1 index=-1 succ=35,-1 in=0000000000000000:0000000000080070 out=0000000000000000:0000000000180070 def=0000000000000000:0000000000100000 use=0000000000000000:0000000000000000
; wide node=35 op=385 line=10 depth=0 result=-1 left=-1 right=-1 index=-1 succ=36,-1 in=0000000000000000:0000000000180070 out=0000000000000000:0000000000180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=36 op=385 line=10 depth=1 result=-1 left=-1 right=-1 index=-1 succ=37,-1 in=0000000000000000:0000000000180070 out=0000000000000000:0000000000180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=37 op=60 line=10 depth=1 result=21 left=20 right=4 index=-1 succ=38,-1 in=0000000000000000:0000000000180070 out=0000000000000000:0000000000380070 def=0000000000000000:0000000000200000 use=0000000000000000:0000000000100010
; wide node=38 op=362 line=10 depth=1 result=-1 left=21 right=-1 index=-1 succ=39,70 in=0000000000000000:0000000000380070 out=0000000000000000:0000000000180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000200000
; wide node=39 op=61 line=11 depth=1 result=22 left=-1 right=20 index=-1 succ=40,-1 in=0000000000000000:0000000000180070 out=0000000000000000:0000000000580070 def=0000000000000000:0000000000400000 use=0000000000000000:0000000000100000
; wide node=40 op=380 line=11 depth=1 result=23 left=-1 right=22 index=-1 succ=41,-1 in=0000000000000000:0000000000580070 out=0000000000000000:0000000000980070 def=0000000000000000:0000000000800000 use=0000000000000000:0000000000400000
; wide node=41 op=371 line=11 depth=1 result=24 left=23 right=-1 index=-1 succ=42,-1 in=0000000000000000:0000000000980070 out=0000000000000000:0000000001180070 def=0000000000000000:0000000001000000 use=0000000000000000:0000000000800000
; wide node=42 op=65539 line=11 depth=1 result=25 left=-1 right=24 index=-1 succ=43,-1 in=0000000000000000:0000000001180070 out=0000000000000000:0000000002180070 def=0000000000000000:0000000002000000 use=0000000000000000:0000000001000000
; wide node=43 op=380 line=11 depth=1 result=26 left=-1 right=25 index=-1 succ=44,-1 in=0000000000000000:0000000002180070 out=0000000000000000:0000000004180070 def=0000000000000000:0000000004000000 use=0000000000000000:0000000002000000
; wide node=44 op=43 line=11 depth=1 result=27 left=6 right=26 index=-1 succ=45,-1 in=0000000000000000:0000000004180070 out=0000000000000000:0000000008180070 def=0000000000000000:0000000008000000 use=0000000000000000:0000000004000040
; wide node=45 op=380 line=11 depth=1 result=28 left=-1 right=27 index=-1 succ=46,-1 in=0000000000000000:0000000008180070 out=0000000000000000:0000000010180070 def=0000000000000000:0000000010000000 use=0000000000000000:0000000008000000
; wide node=46 op=65540 line=12 depth=1 result=-1 left=-1 right=-1 index=-1 succ=47,-1 in=0000000000000000:0000000010180070 out=0000000000000000:0000000010180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=47 op=65536 line=12 depth=1 result=30 left=28 right=-1 index=-1 succ=48,-1 in=0000000000000000:0000000010180070 out=0000000000000000:0000000050180070 def=0000000000000000:0000000040000000 use=0000000000000000:0000000010000000
; wide node=48 op=43 line=12 depth=1 result=31 left=19 right=20 index=-1 succ=49,-1 in=0000000000000000:0000000050180070 out=0000000000000000:00000000d0180070 def=0000000000000000:0000000080000000 use=0000000000000000:0000000000180000
; wide node=49 op=65536 line=12 depth=1 result=32 left=31 right=-1 index=-1 succ=50,-1 in=0000000000000000:00000000d0180070 out=0000000000000000:00000001d0180070 def=0000000000000000:0000000100000000 use=0000000000000000:0000000080000000
; wide node=50 op=94 line=12 depth=1 result=33 left=30 right=32 index=-1 succ=51,-1 in=0000000000000000:00000001d0180070 out=0000000000000000:0000000290180070 def=0000000000000000:0000000200000000 use=0000000000000000:0000000140000000
; wide node=51 op=65537 line=12 depth=1 result=-1 left=28 right=33 index=-1 succ=52,-1 in=0000000000000000:0000000290180070 out=0000000000000000:0000000090180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000000210000000
; wide node=52 op=43 line=13 depth=1 result=34 left=28 right=-1 index=-1 succ=53,-1 in=0000000000000000:0000000090180070 out=0000000000000000:0000000490180070 def=0000000000000000:0000000400000000 use=0000000000000000:0000000010000000
; wide node=53 op=65536 line=13 depth=1 result=35 left=34 right=-1 index=-1 succ=54,-1 in=0000000000000000:0000000490180070 out=0000000000000000:0000000c90180070 def=0000000000000000:0000000800000000 use=0000000000000000:0000000400000000
; wide node=54 op=65536 line=13 depth=1 result=36 left=31 right=-1 index=-1 succ=55,-1 in=0000000000000000:0000000c90180070 out=0000000000000000:0000001c10180070 def=0000000000000000:0000001000000000 use=0000000000000000:0000000080000000
; wide node=55 op=65540 line=13 depth=1 result=-1 left=-1 right=-1 index=-1 succ=56,-1 in=0000000000000000:0000001c10180070 out=0000000000000000:0000001c10180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=56 op=43 line=13 depth=1 result=38 left=35 right=36 index=-1 succ=57,-1 in=0000000000000000:0000001c10180070 out=0000000000000000:0000004410180070 def=0000000000000000:0000004000000000 use=0000000000000000:0000001800000000
; wide node=57 op=43 line=13 depth=1 result=39 left=38 right=-1 index=-1 succ=58,-1 in=0000000000000000:0000004410180070 out=0000000000000000:0000008410180070 def=0000000000000000:0000008000000000 use=0000000000000000:0000004000000000
; wide node=58 op=65537 line=13 depth=1 result=-1 left=34 right=39 index=-1 succ=59,-1 in=0000000000000000:0000008410180070 out=0000000000000000:0000008010180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000008400000000
; wide node=59 op=43 line=14 depth=1 result=40 left=28 right=-1 index=-1 succ=60,-1 in=0000000000000000:0000008010180070 out=0000000000000000:0000018010180070 def=0000000000000000:0000010000000000 use=0000000000000000:0000000010000000
; wide node=60 op=65536 line=14 depth=1 result=41 left=40 right=-1 index=-1 succ=61,-1 in=0000000000000000:0000018010180070 out=0000000000000000:0000038010180070 def=0000000000000000:0000020000000000 use=0000000000000000:0000010000000000
; wide node=61 op=271 line=14 depth=1 result=42 left=41 right=-1 index=-1 succ=62,-1 in=0000000000000000:0000038010180070 out=0000000000000000:0000058010180070 def=0000000000000000:0000040000000000 use=0000000000000000:0000020000000000
; wide node=62 op=65540 line=14 depth=1 result=-1 left=-1 right=-1 index=-1 succ=63,-1 in=0000000000000000:0000058010180070 out=0000000000000000:0000058010180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=63 op=94 line=14 depth=1 result=44 left=42 right=39 index=-1 succ=64,-1 in=0000000000000000:0000058010180070 out=0000000000000000:0000110010180070 def=0000000000000000:0000100000000000 use=0000000000000000:0000048000000000
; wide node=64 op=65536 line=14 depth=1 result=45 left=28 right=-1 index=-1 succ=65,-1 in=0000000000000000:0000110010180070 out=0000000000000000:0000310000180070 def=0000000000000000:0000200000000000 use=0000000000000000:0000000010000000
; wide node=65 op=65540 line=14 depth=1 result=-1 left=-1 right=-1 index=-1 succ=66,-1 in=0000000000000000:0000310000180070 out=0000000000000000:0000310000180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=66 op=94 line=14 depth=1 result=47 left=44 right=45 index=-1 succ=67,-1 in=0000000000000000:0000310000180070 out=0000000000000000:0000810000180070 def=0000000000000000:0000800000000000 use=0000000000000000:0000300000000000
; wide node=67 op=65537 line=14 depth=1 result=-1 left=40 right=47 index=-1 succ=68,-1 in=0000000000000000:0000810000180070 out=0000000000000000:0000000000180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000810000000000
; wide node=68 op=43 line=10 depth=1 result=20 left=20 right=-1 index=-1 succ=69,-1 in=0000000000000000:0000000000180070 out=0000000000000000:0000000000180070 def=0000000000000000:0000000000100000 use=0000000000000000:0000000000100000
; wide node=69 op=350 line=10 depth=1 result=-1 left=-1 right=-1 index=-1 succ=36,-1 in=0000000000000000:0000000000180070 out=0000000000000000:0000000000180070 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=70 op=385 line=10 depth=0 result=-1 left=-1 right=-1 index=-1 succ=71,-1 in=0000000000000000:0000000000000060 out=0000000000000000:0000000000000060 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=71 op=61 line=16 depth=0 result=48 left=-1 right=-1 index=-1 succ=72,-1 in=0000000000000000:0000000000000060 out=0000000000000000:0001000000000060 def=0000000000000000:0001000000000000 use=0000000000000000:0000000000000000
; wide node=72 op=385 line=16 depth=0 result=-1 left=-1 right=-1 index=-1 succ=73,-1 in=0000000000000000:0001000000000060 out=0000000000000000:0001000000000060 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=73 op=385 line=16 depth=1 result=-1 left=-1 right=-1 index=-1 succ=74,-1 in=0000000000000000:0001000000000060 out=0000000000000000:0001000000000060 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=74 op=65539 line=16 depth=1 result=49 left=48 right=-1 index=-1 succ=75,-1 in=0000000000000000:0001000000000060 out=0000000000000000:0003000000000060 def=0000000000000000:0002000000000000 use=0000000000000000:0001000000000000
; wide node=75 op=43 line=16 depth=1 result=50 left=6 right=49 index=-1 succ=76,-1 in=0000000000000000:0003000000000060 out=0000000000000000:0005000000000060 def=0000000000000000:0004000000000000 use=0000000000000000:0002000000000040
; wide node=76 op=65540 line=16 depth=1 result=-1 left=-1 right=-1 index=-1 succ=77,-1 in=0000000000000000:0005000000000060 out=0000000000000000:0005000000000060 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=77 op=65536 line=16 depth=1 result=52 left=50 right=-1 index=-1 succ=78,-1 in=0000000000000000:0005000000000060 out=0000000000000000:0015000000000060 def=0000000000000000:0010000000000000 use=0000000000000000:0004000000000000
; wide node=78 op=65540 line=16 depth=1 result=-1 left=-1 right=-1 index=-1 succ=79,-1 in=0000000000000000:0015000000000060 out=0000000000000000:0015000000000060 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=79 op=65536 line=16 depth=1 result=54 left=50 right=-1 index=-1 succ=80,-1 in=0000000000000000:0015000000000060 out=0000000000000000:0055000000000060 def=0000000000000000:0040000000000000 use=0000000000000000:0004000000000000
; wide node=80 op=380 line=16 depth=1 result=55 left=-1 right=54 index=-1 succ=81,-1 in=0000000000000000:0055000000000060 out=0000000000000000:0095000000000060 def=0000000000000000:0080000000000000 use=0000000000000000:0040000000000000
; wide node=81 op=271 line=16 depth=1 result=56 left=55 right=-1 index=-1 succ=82,-1 in=0000000000000000:0095000000000060 out=0000000000000000:0115000000000060 def=0000000000000000:0100000000000000 use=0000000000000000:0080000000000000
; wide node=82 op=43 line=16 depth=1 result=57 left=52 right=56 index=-1 succ=83,-1 in=0000000000000000:0115000000000060 out=0000000000000000:0205000000000060 def=0000000000000000:0200000000000000 use=0000000000000000:0110000000000000
; wide node=83 op=65540 line=16 depth=1 result=-1 left=-1 right=-1 index=-1 succ=84,-1 in=0000000000000000:0205000000000060 out=0000000000000000:0205000000000060 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=84 op=65536 line=16 depth=1 result=59 left=50 right=-1 index=-1 succ=85,-1 in=0000000000000000:0205000000000060 out=0000000000000000:0a01000000000060 def=0000000000000000:0800000000000000 use=0000000000000000:0004000000000000
; wide node=85 op=65540 line=16 depth=1 result=-1 left=-1 right=-1 index=-1 succ=86,-1 in=0000000000000000:0a01000000000060 out=0000000000000000:0a01000000000060 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=86 op=43 line=16 depth=1 result=61 left=57 right=59 index=-1 succ=87,-1 in=0000000000000000:0a01000000000060 out=0000000000000000:2001000000000060 def=0000000000000000:2000000000000000 use=0000000000000000:0a00000000000000
; wide node=87 op=94 line=16 depth=1 result=5 left=5 right=61 index=-1 succ=88,-1 in=0000000000000000:2001000000000060 out=0000000000000000:0001000000000060 def=0000000000000000:0000000000000020 use=0000000000000000:2000000000000020
; wide node=88 op=43 line=16 depth=1 result=48 left=48 right=-1 index=-1 succ=89,-1 in=0000000000000000:0001000000000060 out=0000000000000000:0001000000000060 def=0000000000000000:0001000000000000 use=0000000000000000:0001000000000000
; wide node=89 op=60 line=16 depth=1 result=62 left=48 right=-1 index=-1 succ=90,-1 in=0000000000000000:0001000000000060 out=0000000000000000:4001000000000060 def=0000000000000000:4000000000000000 use=0000000000000000:0001000000000000
; wide node=90 op=362 line=16 depth=1 result=-1 left=62 right=-1 index=-1 succ=91,73 in=0000000000000000:4001000000000060 out=0000000000000000:0001000000000060 def=0000000000000000:0000000000000000 use=0000000000000000:4000000000000000
; wide node=91 op=353 line=17 depth=0 result=-1 left=5 right=-1 index=-1 succ=-1,-1 in=0000000000000000:0000000000000020 out=0000000000000000:0000000000000000 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000020
; wide node=92 op=385 line=17 depth=0 result=-1 left=-1 right=-1 index=-1 succ=93,-1 in=0000000000000000:0000000000000000 out=0000000000000000:0000000000000000 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
; wide node=93 op=377 line=18 depth=0 result=-1 left=-1 right=-1 index=-1 succ=-1,-1 in=0000000000000000:0000000000000000 out=0000000000000000:0000000000000000 def=0000000000000000:0000000000000000 use=0000000000000000:0000000000000000
	add	spx,#36
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
	mov	dr12,dr4
	mov	dr16,spx
	sub	dr16,#35
	anl	wr16,#0x00ff
	mov	@spx-0x0007,wr16
	mov	@spx-0x0005,wr18
; wide byte copy
	mov	r3,#0x00
__wm____m251r4_struct_records_61:
__wm____m251r4_struct_records_4:
	movz	wr16,r3
	mov	wr20,#0x0007
; wide unsigned multiply 8x8 -> 16
	mul	wr16,wr20
	mov	wr24,wr18
	mov	wr26,wr24
	sub	wr24,wr24
	mov	wr20,@spx-0x0007
	mov	wr22,@spx-0x0005
	add	dr24,dr20
	anl	wr24,#0x00ff
; wide memory store width=1 volatile-bytes=0
	mov	@dr24,r3
	mov	wr28,wr6
	movz	wr20,r3
	add	wr28,wr20
; wide indexed address op=+ index-width=4
	mov	dr16,dr24
	add	dr16,#0x0001
	anl	wr16,#0x00ff
; wide memory store width=2 volatile-bytes=0
	mov	@dr16,wr28
	mov	dr28,dr4
	movz	wr22,r3
	sub	wr20,wr20
	add	dr28,dr20
; wide indexed address op=+ index-width=4
	mov	dr16,dr24
	add	dr16,#0x0003
	anl	wr16,#0x00ff
; wide memory store width=4 volatile-bytes=0
	mov	@dr16,wr28
	mov	@dr16+2,wr30
	inc	r3
; wide byte compare signed=0
	cmp	r3,#0x04
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
	cmp	r8,r9
	jc	__wm____m251r4_struct_records_1000002
	ejmp	__wm____m251r4_struct_records_2
__wm____m251r4_struct_records_1000002:
; wide byte copy
	mov	r7,r8
; wide byte copy
; wide byte bitwise op=anl
	anl	r7,#0x03
	mov	wr16,#0x0007
	movz	wr20,r7
; wide unsigned multiply 8x8 -> 8
	mul	wr16,wr20
	mov	wr10,wr18
	mov	r7,r11
; wide byte copy
	movz	wr22,r7
	sub	wr20,wr20
	mov	wr4,@spx-0x0007
	mov	wr6,@spx-0x0005
	add	dr4,dr20
	anl	wr4,#0x00ff
	mov	dr28,dr4
; wide memory load width=1 volatile-bytes=0
	mov	r2,@dr28
	mov	wr4,@spx-0x0003
	mov	wr6,@spx-0x0001
	movz	wr22,r8
	sub	wr20,wr20
	add	dr4,dr20
	anl	wr4,#0x00ff
; wide memory load width=1 volatile-bytes=0
	mov	r3,@dr4
; wide byte bitwise op=xrl
	xrl	r3,r2
; wide memory store width=1 volatile-bytes=0
	mov	@dr28,r3
	mov	dr24,dr28
	add	dr24,#0x0001
	anl	wr24,#0x00ff
; wide memory load width=2 volatile-bytes=0
	mov	wr0,@dr24
; wide memory load width=1 volatile-bytes=0
	mov	r7,@dr4
	movz	wr20,r7
	mov	wr6,wr0
	add	wr6,wr20
	mov	wr2,wr6
	add	wr2,#0x0001
; wide memory store width=2 volatile-bytes=0
	mov	@dr24,wr2
	mov	dr24,dr28
	add	dr24,#0x0003
	anl	wr24,#0x00ff
; wide memory load width=4 volatile-bytes=0
	mov	wr4,@dr24
	mov	wr6,@dr24+2
	add	dr4,dr4
	mov	wr22,wr2
	sub	wr20,wr20
	xrl	wr4,wr20
	xrl	wr6,wr22
; wide memory load width=1 volatile-bytes=0
	mov	r3,@dr28
	movz	wr22,r3
	sub	wr20,wr20
	xrl	wr4,wr20
	xrl	wr6,wr22
; wide memory store width=4 volatile-bytes=0
	mov	@dr24,wr4
	mov	@dr24+2,wr6
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
	mov	wr24,@spx-0x0007
	mov	wr26,@spx-0x0005
	mov	wr22,wr6
	sub	wr20,wr20
	add	dr24,dr20
	anl	wr24,#0x00ff
; wide indexed address op=+ index-width=4
	mov	dr16,dr24
	add	dr16,#0x0003
	anl	wr16,#0x00ff
; wide memory load width=4 volatile-bytes=0
	mov	wr28,@dr16
	mov	wr30,@dr16+2
; wide indexed address op=+ index-width=4
	mov	dr16,dr24
	add	dr16,#0x0001
	anl	wr16,#0x00ff
; wide memory load width=2 volatile-bytes=0
	mov	wr6,@dr16
	sub	wr4,wr4
	mov	r4,r5
	mov	r5,r6
	mov	r6,r7
	mov	r7,#0
	add	dr4,dr28
; wide memory load width=1 volatile-bytes=0
	mov	r9,@dr24
	movz	wr22,r9
	sub	wr20,wr20
	add	dr4,dr20
	xrl	wr12,wr4
	xrl	wr14,wr6
	inc	r8
; wide byte compare signed=0
	cmp	r8,#0x04
	jnc	__wm____m251r4_struct_records_1000003
	ejmp	__wm____m251r4_struct_records_9
__wm____m251r4_struct_records_1000003:
	mov	dr4,dr12
	sub	spx,#36
	eret
__wm____m251r4_struct_records_11:
; mx251-effects schema=1 function=___m251r4_struct_records abi=4 complete=0 reads=ffffffff writes=ffffffff specialReads=ffffffff specialWrites=ffffffff flagsReads=ffffffff flagsWrites=ffffffff memory=3 instructions=147 unknown=23 calls=0
	.area CSEG    (CODE)
	.area CONST   (CODE)
	.area XINIT   (CODE)
	.area CABS    (ABS,CODE)
