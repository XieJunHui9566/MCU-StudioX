#include <8052.h>
#include "rt51.h"

/* 使用标准 8052 Timer2：验证用户时间源回调与向量 5，和 STC15 示例的 Timer0 分开。 */
RT51_TICK_ISR(system_tick, 5)
volatile __xdata uint8_t test_result, stage, check_count;
static __xdata uint8_t ids[3], received, item_a, item_b, item_c, ring[2];
static __xdata rt51_sem_t sem;
static __xdata rt51_mutex_t mutex, mutex2;
static __xdata rt51_queue_t queue;
volatile __xdata uint16_t counters[3];
void test_finish(void) { EA = 0; TR2 = 0; for (;;) {} }
static void expect(uint8_t ok, uint8_t code)
{
    if (!ok) { test_result = code; test_finish(); }
    ++check_count;
}
static void finish(void) { test_result = 0x5a; test_finish(); }
static void timer_init(void)
{
    RCAP2H = TH2 = 0xc0; RCAP2L = TL2 = 0;
    T2CON = 4; ET2 = 1;
}
static void timer_ack(void)
{
    TF2 = 0;
#if TEST_CASE == 16
    /* 真实 tick ISR 上下文中依次测试三个 FromISR 入口。 */
    if (!stage) { rt51_sem_give_from_isr(&sem); stage = 1; }
    else if (stage == 1) { rt51_event_post_from_isr(0, 8); stage = 2; }
    else if (stage == 2) { rt51_queue_send_from_isr(&queue, &item_b); stage = 3; }
#endif
}
static void bad_timer_init(void) { EA = 1; }

#if TEST_CASE == 1
/* 16 位 tick 回绕、delay(0)、EA 保留、返回任务自动终止、空闲回退。 */
static void first(void)
{
    uint16_t before;
    uint8_t ea;
    before = rt51_ticks();
    expect(rt51_delay(3) == RT51_OK, 1);
    expect((uint16_t)(rt51_ticks() - before) >= 3, 2);
    expect(rt51_ticks() < 10, 3);
    ea = rt51_critical_enter();
    expect(ea && !EA, 4);
    expect(rt51_delay(1) == RT51_CONTEXT && !EA, 5);
    rt51_critical_exit(ea);
    expect(EA, 6);
    rt51_delay(0);
    stage = 1;
}
static void second(void)
{
    while (!stage) rt51_delay(1);
    rt51_delay(1);
    expect(rt51_tasks[0].state == RT51_FINISHED, 7);
    finish();
}
static void third(void) { ++counters[2]; }
#elif TEST_CASE == 2
/* 事件 ANY/ALL、自动消费、保留未匹配位、有限/无限等待。 */
static void first(void)
{
    expect(rt51_event_wait(3, RT51_EVENT_ALL, 5, &received) == RT51_OK && received == 3, 11);
    expect(rt51_event_wait(4, RT51_EVENT_ANY, 0, &received) == RT51_OK && received == 4, 12);
    expect(rt51_event_wait(1, RT51_EVENT_ANY, 2, &received) == RT51_TIMEOUT && !received, 13);
    stage = 1;
    expect(rt51_event_wait(8, RT51_EVENT_ANY, RT51_FOREVER, &received) == RT51_OK && received == 8, 14);
    expect(rt51_event_wait(0, 0, 0, &received) == RT51_INVALID, 15);
    finish();
}
static void second(void)
{
    rt51_event_post(0, 5);
    rt51_delay(1);
    expect(rt51_tasks[0].state == RT51_EVENT, 16);
    rt51_event_post(0, 2);
    while (!stage) rt51_delay(1);
    expect(rt51_event_post_from_isr(0, 8) == RT51_OK, 17);
}
static void third(void) { for (;;) ++counters[2]; }
#elif TEST_CASE == 3
static void first(void)
{
    expect(rt51_sem_take(&sem, 5) == RT51_OK, 21);
    expect(!sem.count, 22);
    expect(rt51_sem_take(&sem, 2) == RT51_TIMEOUT, 23);
    expect(rt51_sem_give(&sem) == RT51_OK, 24);
    expect(rt51_sem_give(&sem) == RT51_FULL, 25);
    expect(rt51_sem_take(&sem, 0) == RT51_OK, 26);
    expect(rt51_sem_take(&sem, 0) == RT51_TIMEOUT, 27);
    finish();
}
static void second(void) { rt51_sem_give(&sem); }
static void third(void) { for (;;) ++counters[2]; }
#elif TEST_CASE == 4
static void first(void)
{
    expect(rt51_queue_receive(&queue, &item_a, 5) == RT51_OK && item_a == 0x12, 31);
    rt51_delay(1);
    expect(rt51_queue_receive(&queue, &item_a, 0) == RT51_OK && item_a == 0x34, 32);
    expect(rt51_queue_receive(&queue, &item_a, 0) == RT51_OK && item_a == 0x56, 33);
    expect(rt51_queue_receive(&queue, &item_a, 0) == RT51_OK && item_a == 0x78, 34);
    expect(rt51_queue_receive(&queue, &item_a, 1) == RT51_TIMEOUT, 35);
    expect(rt51_queue_send_from_isr(&queue, &item_a) == RT51_OK, 36);
    expect(rt51_queue_receive(&queue, &item_b, 0) == RT51_OK && item_a == item_b, 37);
    finish();
}
static void second(void)
{
    item_b = 0x12; rt51_queue_send(&queue, &item_b, RT51_FOREVER);
    item_b = 0x34; rt51_queue_send(&queue, &item_b, RT51_FOREVER);
    item_b = 0x56; rt51_queue_send(&queue, &item_b, RT51_FOREVER);
    item_b = 0x78;
    expect(rt51_queue_send(&queue, &item_b, 0) == RT51_FULL, 38);
    expect(rt51_queue_send(&queue, &item_b, 5) == RT51_OK, 39);
}
static void third(void) { for (;;) ++counters[2]; }
#elif TEST_CASE == 5
/* H 等待 L 的锁；M 不让出，L 仍因继承 H 的优先级运行。 */
static void first(void)
{
    rt51_delay(2);
    expect(rt51_mutex_lock(&mutex, 10) == RT51_OK, 41);
    expect(stage == 2, 42);
    expect(rt51_mutex_lock(&mutex, 0) == RT51_DEADLOCK, 43);
    expect(rt51_mutex_unlock(&mutex) == RT51_OK, 44);
    expect(rt51_mutex_unlock(&mutex) == RT51_OWNER, 45);
    finish();
}
static void second(void)
{
    rt51_delay(1);
    for (;;) ++counters[1];
}
static void third(void)
{
    expect(rt51_mutex_lock(&mutex, 0) == RT51_OK, 46);
    stage = 1;
    while (rt51_tasks[2].effective_priority != 1) ++counters[2];
    stage = 2;
    expect(rt51_mutex_unlock(&mutex) == RT51_OK, 47);
    for (;;) ++counters[2];
}
#elif TEST_CASE == 6
/* 锁等待超时必须撤销继承，即使此前的持锁任务从不让出。 */
static void first(void)
{
    rt51_delay(1);
    expect(rt51_mutex_lock(&mutex, 2) == RT51_TIMEOUT, 51);
    expect(rt51_tasks[2].effective_priority == 10, 52);
    finish();
}
static void second(void) { for (;;) rt51_delay(1); }
static void third(void)
{
    rt51_mutex_lock(&mutex, 0);
    for (;;) ++counters[2];
}
#elif TEST_CASE == 7
/* 嵌套继承和锁依赖环检查。 */
static void first(void)
{
    rt51_delay(2);
    expect(rt51_mutex_lock(&mutex, 10) == RT51_OK, 61);
    expect(stage == 3, 62);
    rt51_mutex_unlock(&mutex);
    finish();
}
static void second(void)
{
    rt51_mutex_lock(&mutex, 0);
    rt51_delay(1);
    expect(rt51_mutex_lock(&mutex2, 10) == RT51_OK, 63);
    rt51_mutex_unlock(&mutex2);
    rt51_mutex_unlock(&mutex);
}
static void third(void)
{
    rt51_mutex_lock(&mutex2, 0);
    while (rt51_tasks[2].effective_priority != 1) ++counters[2];
    expect(rt51_mutex_lock(&mutex, 0) == RT51_DEADLOCK, 64);
    stage = 3;
    rt51_mutex_unlock(&mutex2);
}
#elif TEST_CASE == 8
/* 三个同优先级纯计算任务，嵌套 C 栈、自动 bit 和算术辅助函数都须独立。 */
static uint16_t nested(uint16_t value, uint8_t n)
{
    volatile uint16_t local = value;
    __bit flag = value & 1;
    uint8_t i;
    for (i = 0; i < n; ++i) local += flag ? 3 : 2;
    return local;
}
static void arithmetic(uint16_t seed)
{
    volatile uint32_t value = ((uint32_t)seed << 16) | 0xab;
    uint32_t quotient = value / 257UL;
    uint32_t remainder = value % 257UL;
    expect(quotient * 257UL + remainder == value, 74);
    expect(quotient == (seed == 0x1234 ? 1188318UL : seed == 0x3211 ? 3268385UL : 7870695UL), 75);
    expect(remainder == (seed == 0x1234 ? 205UL : seed == 0x3211 ? 138UL : 196UL), 76);
}
static void compute(uint8_t which, uint16_t seed)
{
    uint16_t i;
    for (i = 0; i < 100; ++i) {
        expect(nested(seed, 9) == seed + ((seed & 1) ? 27 : 18), 71 + which);
        arithmetic(seed);
        ++counters[which];
    }
    while (!counters[0] || !counters[1] || !counters[2]) { }
    if (rt51_ticks() >= 3) finish();
    for (;;) { if (rt51_ticks() >= 3) finish(); }
}
static void first(void) { compute(0, 0x1234); }
static void second(void) { compute(1, 0x3211); }
static void third(void) { compute(2, 0x7891); }
#elif TEST_CASE == 9
/* 队列满等待超时后，原消息不得迟到入队。 */
static void first(void)
{
    item_a = 1; expect(rt51_queue_send(&queue, &item_a, 0) == RT51_OK, 81);
    item_a = 2; expect(rt51_queue_send(&queue, &item_a, 0) == RT51_OK, 82);
    item_a = 3; expect(rt51_queue_send(&queue, &item_a, 2) == RT51_TIMEOUT, 83);
    expect(rt51_queue_receive(&queue, &item_a, 0) == RT51_OK && item_a == 1, 84);
    expect(rt51_queue_receive(&queue, &item_a, 0) == RT51_OK && item_a == 2, 85);
    expect(!queue.count && rt51_queue_receive(&queue, &item_a, 0) == RT51_TIMEOUT, 86);
    finish();
}
static void second(void) { for (;;) ++counters[1]; }
static void third(void) { for (;;) ++counters[2]; }
#elif TEST_CASE == 10
static void first(void)
{
    volatile uint8_t too_large[90];
    too_large[0] = 1;
    rt51_yield();
    test_result = too_large[0];
    test_finish();
}
static void second(void) { for (;;) {} }
static void third(void) { for (;;) {} }
#elif TEST_CASE == 11
static void first(void) __naked
{
    __asm
        clr _EA
        mov r0, #0x11
        mov r1, #0x22
        mov r2, #0x33
        mov r3, #0x44
        mov r4, #0x55
        mov r5, #0x66
        mov r6, #0x77
        mov r7, #0x88
        mov b, #0x5a
        mov dptr, #0x1234
        mov _bp, #0x66
        mov 0x20, #0xab
        mov a, #0xa5
        mov psw, #0xc4
        setb _EA
01101$:
        nop
        nop
        nop
        clr _EA
        push psw
        cjne a, #0xa5, 01102$
        cjne r0, #0x11, 01102$
        cjne r1, #0x22, 01102$
        cjne r2, #0x33, 01102$
        cjne r3, #0x44, 01102$
        cjne r4, #0x55, 01102$
        cjne r5, #0x66, 01102$
        cjne r6, #0x77, 01102$
        cjne r7, #0x88, 01102$
        mov a, b
        cjne a, #0x5a, 01102$
        mov a, dpl
        cjne a, #0x34, 01102$
        mov a, dph
        cjne a, #0x12, 01102$
        mov a, _bp
        cjne a, #0x66, 01102$
        mov a, 0x20
        cjne a, #0xab, 01102$
        pop acc
        anl a, #0xc4
        cjne a, #0xc4, 01102$
        mov dptr, #_stage
        mov a, #1
        movx @dptr, a
        mov dptr, #0x1234
        mov a, #0xa5
        mov psw, #0xc4
        setb _EA
        sjmp 01101$
01102$:
        mov dptr, #_test_result
        mov a, #111
        movx @dptr, a
        ljmp _test_finish
    __endasm;
}
static void second(void)
{
    __asm mov 0x20, #0xcd __endasm;
    while (rt51_ticks() < 10) ++counters[1];
    expect(stage && counters[1], 112);
    finish();
}
static void third(void) { for (;;) ++counters[2]; }
#elif TEST_CASE == 12
static void first(void) __naked
{
    __asm
        mov psw, #8
        ljmp _rt51_yield
    __endasm;
}
static void second(void) { for (;;) {} }
static void third(void) { for (;;) {} }
#elif TEST_CASE == 13
static void first(void) { finish(); }
static void second(void) { for (;;) {} }
static void third(void) { for (;;) {} }
#elif TEST_CASE == 14
static void first(void) { rt51_mutex_lock(&mutex, 0); }
static void second(void) { for (;;) {} }
static void third(void) { for (;;) {} }
#elif TEST_CASE == 15
static void first(void) { ++stage; }
static void second(void) { ++stage; }
static void third(void) { expect(stage == 2, 121); test_result = 0x5a; }
#elif TEST_CASE == 16
static void first(void)
{
    expect(rt51_sem_take(&sem, RT51_FOREVER) == RT51_OK, 131);
    expect(rt51_event_wait(8, RT51_EVENT_ANY, RT51_FOREVER, &received) == RT51_OK && received == 8, 132);
    expect(rt51_queue_receive(&queue, &item_a, RT51_FOREVER) == RT51_OK && item_a == 0x42, 133);
    finish();
}
static void second(void) { for (;;) ++counters[1]; }
static void third(void) { for (;;) ++counters[2]; }
#endif

void main(void)
{
    rt51_init();
    expect(rt51_sem_init(&sem, 0, 1) == RT51_OK, 91);
    expect(rt51_mutex_init(&mutex) == RT51_OK && rt51_mutex_init(&mutex2) == RT51_OK, 92);
    expect(rt51_queue_init(&queue, ring, sizeof(ring), 2, 1) == RT51_OK, 93);
    expect(rt51_queue_init(&queue, ring, 1, 2, 1) == RT51_INVALID, 94);
    expect(rt51_start(0, 0) == RT51_INVALID, 95);
    expect(rt51_task_create(first, 1, &ids[0]) == RT51_OK, 96);
    expect(rt51_task_create(second, 5, &ids[1]) == RT51_OK, 97);
    expect(rt51_task_create(third, 10, &ids[2]) == RT51_OK, 98);
    expect(rt51_task_create(first, 1, &item_c) == RT51_FULL, 99);
#if TEST_CASE == 1
    rt51_tick_count = 0xfffe;
#elif TEST_CASE == 8 || TEST_CASE == 11
    rt51_tasks[0].priority = rt51_tasks[0].effective_priority = 1;
    rt51_tasks[1].priority = rt51_tasks[1].effective_priority = 1;
    rt51_tasks[2].priority = rt51_tasks[2].effective_priority = 1;
#endif
#if TEST_CASE == 13
    expect(rt51_start(bad_timer_init, 0) == RT51_CONTEXT && !EA && !rt51_running, 122);
#endif
#if TEST_CASE == 16
    item_b = 0x42;
#endif
    rt51_start(timer_init, timer_ack);
    test_result = 100;
    test_finish();
}
