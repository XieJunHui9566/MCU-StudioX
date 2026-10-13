/* IAP15/STC15 板级 C 启动适配，不属于 RT51 调度器。
 * 单 DPTR 搬运 XINIT 到完整 16 位 XRAM 地址；不依赖 MOVX @Ri 的分页，
 * 不改 P2（本板 P2 连接锁存器选择总线），也不切换器件特定的双 DPTR。
 * 此目标文件须与 main 一起链接，用 __mcs51_genXINIT 替换 SDCC 默认副本。
 * GSINIT3 由启动流程顺序执行；不能从 C/任务/ISR 调用下方链接锚点。 */
#if !defined(__SDCC_mcs51)
#error This startup adapter requires SDCC MCS51
#endif
void stc15_xinit_link_anchor(void) __naked
{
    __asm
        .globl __mcs51_genXINIT
        .globl s_XINIT
        .globl l_XINIT
        .globl s_XISEG
        .area GSINIT3 (CODE)
__mcs51_genXINIT::
        mov r6, #l_XINIT
        mov r7, #(l_XINIT >> 8)
        mov r2, #s_XINIT
        mov r3, #(s_XINIT >> 8)
        mov r0, #s_XISEG
        mov r1, #(s_XISEG >> 8)
stc15_xinit_loop:
        mov a, r6
        orl a, r7
        jz stc15_xinit_done
        mov dpl, r2
        mov dph, r3
        clr a
        movc a, @a+dptr
        mov r4, a
        inc dptr
        mov r2, dpl
        mov r3, dph
        mov dpl, r0
        mov dph, r1
        mov a, r4
        movx @dptr, a
        inc dptr
        mov r0, dpl
        mov r1, dph
        mov a, r6
        jnz stc15_xinit_low
        dec r7
stc15_xinit_low:
        dec r6
        sjmp stc15_xinit_loop
stc15_xinit_done:
        .area CSEG (CODE)
    __endasm;
}
