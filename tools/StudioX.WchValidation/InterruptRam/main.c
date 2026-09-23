/* CH32V307 RAM-only interrupt fixture; no clocks, GPIO, flash or option-byte writes.
 * PFIC register offsets/IRQ numbers follow WCH core_riscv.h and ch32v30x.h.
 */
typedef unsigned u32;
#define WORD(address) (*(volatile u32 *)(address))
#define BYTE(address) (*(volatile unsigned char *)(address))
#define PENDING(mask) (WORD(0xe000e204u) = (mask))
#define RESULTS ((volatile u32 *)0x2000d000u)
#define SNAPSHOT ((volatile u32 *)0x2000d200u)
enum { MAGIC, STATE, PHASE, ITERATION, LOW, MID, HIGH, TOP, ERRORS, ERROR_CODE, TRACE, DEPTH, CSR_VALUE, BAD_REGISTER };
extern void register_probe(void);
extern void clobber_callers(void);
extern void (*vectors[])(void);

static void fail(u32 code)
{
    RESULTS[ERRORS]++;
    if (!RESULTS[ERROR_CODE]) RESULTS[ERROR_CODE] = code;
}
static void mark(u32 value) { RESULTS[TRACE] = (RESULTS[TRACE] << 4) | value; }
static void wait_count(unsigned field, u32 before)
{
    for (u32 i = 0; i < 100000; ++i)
        if (RESULTS[field] != before) return;
    fail(0x100u + field);
}
void Default_Handler(void) __attribute__((interrupt("WCH-Interrupt-fast")));
void TIM2_IRQHandler(void) __attribute__((interrupt("WCH-Interrupt-fast")));
void TIM3_IRQHandler(void) __attribute__((interrupt("WCH-Interrupt-fast")));
void TIM4_IRQHandler(void) __attribute__((interrupt("WCH-Interrupt-fast")));
void Software_Handler(void) __attribute__((interrupt()));
void Software_Top_Handler(void) __attribute__((interrupt()));

void Default_Handler(void)
{
    RESULTS[STATE] = 0xbad00001;
    __asm volatile("csrr %0, mcause" : "=r"(RESULTS[ERROR_CODE]));
    for (;;) __asm volatile("nop");
}
void TIM2_IRQHandler(void)
{
    if (RESULTS[DEPTH] != 0) fail(1);
    RESULTS[DEPTH] = 1;
    mark(1); RESULTS[LOW]++;
    clobber_callers();
    if (RESULTS[PHASE] == 2 || RESULTS[PHASE] == 4)
    {
        u32 before = RESULTS[MID];
        PENDING(1u << 13);
        wait_count(MID, before);
        if (RESULTS[DEPTH] != 1) fail(2);
        mark(RESULTS[PHASE] == 4 ? 7 : 5);
    }
    RESULTS[DEPTH] = 0;
}
void TIM3_IRQHandler(void)
{
    if (RESULTS[DEPTH] != 1) fail(3);
    RESULTS[DEPTH] = 2;
    mark(2); RESULTS[MID]++;
    clobber_callers();
    u32 before = RESULTS[HIGH];
    PENDING(1u << 14);
    wait_count(HIGH, before);
    if (RESULTS[DEPTH] != 2) fail(4);
    mark(RESULTS[PHASE] == 4 ? 6 : 4);
    RESULTS[DEPTH] = 1;
}
void TIM4_IRQHandler(void)
{
    if (RESULTS[DEPTH] != 2) fail(5);
    RESULTS[DEPTH] = 3;
    mark(3); RESULTS[HIGH]++;
    clobber_callers();
    if (RESULTS[PHASE] == 4)
    {
        u32 before = RESULTS[TOP];
        WORD(0xe000e208u) = 1u << 2; /* TIM5 IRQ 66 */
        wait_count(TOP, before);
        if (RESULTS[DEPTH] != 3) fail(6);
        mark(5);
    }
    RESULTS[DEPTH] = 2;
}
void Software_Handler(void)
{
    RESULTS[LOW]++;
    clobber_callers();
    mark(1);
}
void Software_Top_Handler(void)
{
    if (RESULTS[DEPTH] != 3) fail(7);
    mark(4); RESULTS[TOP]++;
    clobber_callers();
}
static void interrupts_off(void)
{
    __asm volatile("li t0, 0x88; csrc 0x800, t0; fence.i" ::: "t0", "memory");
}
static void interrupts_on(void)
{
    __asm volatile("li t0, 0x88; csrs 0x800, t0" ::: "t0", "memory");
}
int main(void)
{
    interrupts_off();
    for (u32 i = 0; i < 32; ++i) RESULTS[i] = 0;
    RESULTS[MAGIC] = 0x3071abcd; RESULTS[STATE] = 1;
    WORD(0xe000f000u) = 0; /* SysTick disabled */
    for (u32 i = 0; i < 8; ++i)
    {
        WORD(0xe000e180u + i * 4) = 0xffffffffu;
        WORD(0xe000e280u + i * 4) = 0xffffffffu;
    }
    BYTE(0xe000e400u + 44) = 0xc0;
    BYTE(0xe000e400u + 45) = 0x80;
    BYTE(0xe000e400u + 46) = 0x40;
    BYTE(0xe000e400u + 66) = 0;
    WORD(0xe000e104u) = 7u << 12;
    WORD(0xe000e108u) = 1u << 2;
    for (u32 phase = 1; phase <= 4; ++phase)
    {
        interrupts_off();
        RESULTS[PHASE] = phase;
        vectors[44] = phase == 3 ? Software_Handler : TIM2_IRQHandler;
        u32 config = phase == 3 ? 0x0a : phase == 4 ? 0x1b : 0x0b;
        __asm volatile("csrw 0x804, %0; fence.i" :: "r"(config) : "memory");
        __asm volatile("csrr %0, 0x804" : "=r"(RESULTS[CSR_VALUE]));
        interrupts_on();
        for (u32 i = 0; i < 1000; ++i)
        {
            u32 before = RESULTS[LOW];
            RESULTS[ITERATION] = i + 1; RESULTS[TRACE] = 0;
            register_probe();
            if (RESULTS[LOW] != before + 1 || RESULTS[DEPTH] != 0) fail(8);
            u32 expected = phase == 2 ? 0x12345 : phase == 4 ? 0x1234567 : 1;
            if (RESULTS[TRACE] != expected) fail(9);
            for (u32 reg = 0; reg < 32; ++reg)
                if (SNAPSHOT[reg] != SNAPSHOT[32 + reg]) { fail(0x200u + reg); RESULTS[BAD_REGISTER] = reg; }
            if (RESULTS[ERRORS]) goto finished;
        }
    }
finished:
    interrupts_off();
    RESULTS[STATE] = RESULTS[ERRORS] ? 0xbad00002 : 0x600d600d;
    for (;;) __asm volatile("nop");
}
