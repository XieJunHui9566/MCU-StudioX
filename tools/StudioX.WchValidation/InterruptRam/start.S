.section .vectors,"aw",@progbits
.balign 1024
.global vectors
vectors:
.rept 44
    .word Default_Handler
.endr
    .word TIM2_IRQHandler
    .word TIM3_IRQHandler
    .word TIM4_IRQHandler
.rept 19
    .word Default_Handler
.endr
    .word Software_Top_Handler
.rept 45
    .word Default_Handler
.endr

.section .text.start,"ax",@progbits
.global _start
_start:
    .option push
    .option norelax
    la gp, __global_pointer$
    .option pop
    li sp, 0x2000f000
    la t0, _sbss
    la t1, _ebss
1:  bgeu t0, t1, 2f
    sw zero, 0(t0)
    addi t0, t0, 4
    j 1b
2:  li t0, 0x1f
    csrw 0xbc0, t0
    li t0, 0x0b
    csrw 0x804, t0
    la t0, vectors
    ori t0, t0, 3
    csrw mtvec, t0
    li t0, 0x6088
    csrw mstatus, t0
    la t0, main
    csrw mepc, t0
    mret

.section .text.clobber,"ax",@progbits
.global clobber_callers
clobber_callers:
    .irp reg,t0,t1,t2,t3,t4,t5,t6,a0,a1,a2,a3,a4,a5,a6,a7
    li \reg, 0x76543210
    .endr
    ret
