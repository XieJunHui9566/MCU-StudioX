#include <8052.h>
#include "rt51.h"

RT51_TICK_ISR(system_tick, 5)
volatile __xdata uint8_t test_result, check_count, stage, isr_checked;
static __xdata uint8_t ids[3], item, received, ring[2];
static __xdata uint16_t anchor, counters[3], sum;
static __xdata rt51_sem_t sem;
static __xdata rt51_mutex_t mutex;
static __xdata rt51_queue_t queue;
static __xdata rt51_task_info_t task_info;
static __xdata rt51_kernel_info_t kernel_info;
#if RT51_MAX_TIMERS > 0
static __xdata rt51_timer_t timers[RT51_MAX_TIMERS + 1];
static __xdata rt51_timer_info_t timer_info;
#endif

static void finish(void) { test_result = 0x5a; EA = 0; TR2 = 0; for (;;) { } }
static void expect(uint8_t ok, uint8_t code)
{
    if (!ok) { test_result = code; EA = 0; TR2 = 0; for (;;) { } }
    ++check_count;
}
static void timer_init(void)
{
    RCAP2H = TH2 = 0xc0; RCAP2L = TL2 = 0;
    T2CON = 4; ET2 = 1;
}
static void timer_ack(void)
{
    TF2 = 0;
#if TEST_CASE == 26 && RT51_MAX_TIMERS > 0
    if (!isr_checked) {
        isr_checked = rt51_timer_start(&timers[0], 1) == RT51_CONTEXT ? 1 : 2;
    }
#endif
}
static void third(void)
{
#if TEST_CASE == 22
    expect(rt51_mutex_lock(&mutex, 0) == RT51_OK, 160);
    stage = 1;
    for (;;) rt51_delay(1);
#elif TEST_CASE == 30
    for (;;) rt51_timer_dispatch();
#else
    for (;;) ++counters[2];
#endif
}

#if TEST_CASE == 17
/* 执行耗时不推迟周期锚点，覆盖回绕、逾期和非法参数/上下文。 */
static void first(void)
{
    uint8_t ea;
    anchor = rt51_ticks();
    rt51_delay(2);
    expect(rt51_delay_until(&anchor, 5) == RT51_OK, 1);
    expect(anchor == 1 && rt51_ticks() == 1, 2);
    rt51_delay(2);
    expect(rt51_delay_until(&anchor, 5) == RT51_OK && rt51_ticks() == 6, 3);
    rt51_delay(7);
    expect(rt51_delay_until(&anchor, 5) == RT51_TIMEOUT && anchor == 11, 4);
    expect(rt51_delay_until(0, 5) == RT51_INVALID, 5);
    expect(rt51_delay_until(&anchor, 0) == RT51_INVALID, 6);
    expect(rt51_delay_until(&anchor, 32768u) == RT51_INVALID && anchor == 11, 7);
    ea = rt51_critical_enter();
    expect(rt51_delay_until(&anchor, 1) == RT51_CONTEXT && !EA && anchor == 11, 8);
    rt51_critical_exit(ea);
    finish();
}
static void second(void) { for (;;) ++counters[1]; }
#elif TEST_CASE == 18
static void first(void)
{
    expect(rt51_delay(10) == RT51_ABORTED, 11);
    stage = 1;
    expect(rt51_task_suspend(0) == RT51_OK, 12);
    expect(stage == 2, 13);
    expect(rt51_task_suspend(3) == RT51_INVALID && rt51_task_resume(3) == RT51_INVALID, 14);
    expect(rt51_task_resume(0) == RT51_BUSY, 15);
    finish();
}
static void second(void)
{
    rt51_delay(1);
    expect(rt51_task_suspend(0) == RT51_OK, 16);
    expect(rt51_task_suspend(0) == RT51_BUSY, 17);
    expect(rt51_task_get_info(0, &task_info) == RT51_OK && task_info.state == RT51_SUSPENDED, 18);
    rt51_delay(12);
    expect(!stage && task_info.remaining == 0, 19);
    rt51_task_resume(0);
    expect(stage == 1 && rt51_tasks[0].state == RT51_SUSPENDED, 20);
    stage = 2; rt51_task_resume(0);
}
#elif TEST_CASE == 19
static void first(void)
{
    expect(rt51_sem_take(&sem, RT51_FOREVER) == RT51_ABORTED, 21);
    expect(sem.count == 1 && rt51_sem_take(&sem, 0) == RT51_OK, 22);
    expect(rt51_sem_take(&sem, 0) == RT51_TIMEOUT, 23);
    finish();
}
static void second(void)
{
    expect(rt51_task_suspend(0) == RT51_OK, 24);
    expect(rt51_sem_give_from_isr(&sem) == RT51_OK && sem.count == 1, 25);
    rt51_task_resume(0);
}
#elif TEST_CASE == 20
static void first(void)
{
    item = 1; rt51_queue_send(&queue, &item, 0);
    item = 2; rt51_queue_send(&queue, &item, 0);
    item = 3;
    expect(rt51_queue_send(&queue, &item, RT51_FOREVER) == RT51_ABORTED, 31);
    expect(rt51_queue_receive(&queue, &item, 0) == RT51_OK && item == 2, 32);
    expect(rt51_queue_receive(&queue, &item, 0) == RT51_TIMEOUT, 33);
    finish();
}
static void second(void)
{
    expect(rt51_task_suspend(0) == RT51_OK, 34);
    expect(rt51_queue_receive(&queue, &received, 0) == RT51_OK && received == 1 && queue.count == 1, 35);
    rt51_task_resume(0);
}
#elif TEST_CASE == 21
/* 取消接收/事件等待不得消费新到资源；已经直接移交的许可则保留成功结果。 */
static void first(void)
{
    expect(rt51_queue_receive(&queue, &item, RT51_FOREVER) == RT51_ABORTED, 41);
    expect(rt51_queue_receive(&queue, &item, 0) == RT51_OK && item == 0x42, 42);
    stage = 1;
    expect(rt51_event_wait(8, RT51_EVENT_ANY, RT51_FOREVER, &received) == RT51_ABORTED && !received, 43);
    expect(rt51_event_wait(8, RT51_EVENT_ANY, 0, &received) == RT51_OK && received == 8, 44);
    stage = 2;
    expect(rt51_sem_take(&sem, RT51_FOREVER) == RT51_OK && !sem.count, 45);
    finish();
}
static void second(void)
{
    rt51_task_suspend(0);
    item = 0x42;
    expect(rt51_queue_send_from_isr(&queue, &item) == RT51_OK && queue.count == 1, 46);
    rt51_task_resume(0);
    expect(stage == 1, 47);
    rt51_task_suspend(0);
    expect(rt51_event_post_from_isr(0, 8) == RT51_OK && rt51_tasks[0].events == 8, 48);
    rt51_task_resume(0);
    expect(stage == 2, 49);
    expect(rt51_sem_give_from_isr(&sem) == RT51_OK && !sem.count && rt51_tasks[0].state == RT51_READY, 50);
    rt51_task_suspend(0);
    expect(rt51_tasks[0].result == RT51_OK, 51);
    rt51_task_resume(0);
}
#elif TEST_CASE == 22
static void first(void)
{
    rt51_delay(2);
    expect(stage == 1, 61);
    expect(rt51_mutex_lock(&mutex, RT51_FOREVER) == RT51_ABORTED, 62);
    expect(rt51_tasks[2].effective_priority == 10 && mutex.owner == 2, 63);
    finish();
}
static void second(void)
{
    rt51_delay(3);
    expect(rt51_tasks[0].state == RT51_MUTEX && rt51_tasks[2].effective_priority == 1, 64);
    expect(rt51_task_suspend(2) == RT51_OWNER, 65);
    expect(rt51_task_suspend(0) == RT51_OK && rt51_tasks[2].effective_priority == 10, 66);
    rt51_task_resume(0);
}
#elif TEST_CASE >= 23 && TEST_CASE <= 25
static void callback(void __xdata *context)
{
    uint16_t __xdata *count = context;
    expect(EA && rt51_current == 0, 71);
    expect(rt51_timer_dispatch() == RT51_BUSY, 72);
    ++*count;
#if TEST_CASE == 25
    if (context == &counters[0]) {
        expect(rt51_timer_stop(&timers[1]) == RT51_OK, 73);
        if (*count == 1) expect(rt51_timer_start(&timers[0], 2) == RT51_OK, 74);
        else expect(rt51_timer_stop(&timers[0]) == RT51_OK, 75);
    }
#endif
}
static void first(void)
{
#if TEST_CASE == 23
    rt51_delay(10);
    expect(rt51_timer_get_info(&timers[0], &timer_info) == RT51_OK && timer_info.pending == 3, 81);
    expect(rt51_timer_get_info(&timers[1], &timer_info) == RT51_OK && !timer_info.active && timer_info.pending == 1, 82);
    expect(rt51_timer_dispatch() == RT51_OK && counters[0] == 1 && counters[1] == 1, 83);
    expect(timers[0].pending == 2 && !timers[1].pending, 84);
    expect(rt51_timer_dispatch() == RT51_OK && counters[0] == 2 && counters[1] == 1, 85);
    expect(rt51_timer_stop(&timers[0]) == RT51_OK, 86);
    rt51_delay(5);
    expect(rt51_timer_dispatch() == RT51_OK && counters[0] == 2, 87);
#elif TEST_CASE == 24
    rt51_delay(262);
    expect(rt51_timer_get_info(&timers[0], &timer_info) == RT51_OK && timer_info.pending == 255 && timer_info.overruns >= 7, 91);
    expect(rt51_timer_dispatch() == RT51_OK && counters[0] == 1, 92);
    expect(rt51_timer_stop(&timers[0]) == RT51_OK && !timers[0].pending && !timers[0].active, 93);
    expect(rt51_timer_start(&timers[0], 1) == RT51_OK && timers[0].overruns >= 7, 94);
    rt51_timer_stop(&timers[0]);
#else
    rt51_delay(1);
    expect(rt51_timer_dispatch() == RT51_OK && counters[0] == 1 && !counters[1], 101);
    rt51_delay(2);
    expect(rt51_timer_dispatch() == RT51_OK && counters[0] == 2 && !counters[1], 102);
    rt51_delay(3);
    expect(rt51_timer_dispatch() == RT51_OK && counters[0] == 2, 103);
#endif
    expect(rt51_timer_start(&timers[0], 0) == RT51_INVALID, 104);
    expect(rt51_timer_start(&timers[0], RT51_FOREVER) == RT51_INVALID, 105);
    finish();
}
static void second(void) { for (;;) ++sum; }
#elif TEST_CASE == 27
static void first(void)
{
    anchor = rt51_ticks();
    expect(rt51_delay_until(&anchor, 10) == RT51_ABORTED && anchor == 10, 121);
    expect(rt51_delay_until(&anchor, 5) == RT51_OK && rt51_ticks() == 15, 122);
    finish();
}
static void second(void)
{
    rt51_delay(1); rt51_task_suspend(0); rt51_delay(12); rt51_task_resume(0);
}
#elif TEST_CASE == 28 || TEST_CASE == 29
static void callback(void __xdata *context)
{
    context;
#if TEST_CASE == 28
    expect(rt51_delay(1) == RT51_CONTEXT && EA, 141);
    expect(rt51_task_suspend(0) == RT51_CONTEXT, 142);
    expect(rt51_sem_take(&sem, 0) == RT51_CONTEXT, 143);
    expect(rt51_event_post_from_isr(0, 8) == RT51_OK, 144);
    ++counters[0];
#else
    EA = 0;
#endif
}
static void first(void)
{
    rt51_delay(1);
    expect(rt51_timer_dispatch() == RT51_OK && counters[0] == 1, 145);
    finish();
}
static void second(void) { for (;;) ++counters[1]; }
#elif TEST_CASE == 30
/* 派发任务在回调中被高优先级任务抢占，仍不可重入或挂起派发所有者。 */
static void callback(void __xdata *context)
{
    uint16_t before = rt51_ticks();
    uint16_t __xdata *count = context;
    while ((uint16_t)(rt51_ticks() - before) < 3) { }
    expect(EA && rt51_current == 2 && stage == 1, 151);
    ++*count;
}
static void first(void)
{
    rt51_delay(2);
    expect(rt51_timer_dispatch() == RT51_BUSY, 152);
    expect(rt51_task_suspend(2) == RT51_BUSY, 153);
    stage = 1;
    rt51_delay(3);
    expect(counters[0] == 1, 154);
    finish();
}
static void second(void) { for (;;) rt51_delay(1); }
#elif TEST_CASE == 26
static void callback(void __xdata *context) { context; }
static void first(void)
{
    uint8_t i, ea;
    rt51_delay(5);
    ea = rt51_critical_enter();
    expect(rt51_kernel_get_info(&kernel_info) == RT51_OK && !EA, 111);
    expect(kernel_info.running && !kernel_info.fault && kernel_info.current == 0 && kernel_info.kernel_stack_peak < 80, 112);
    sum = 0;
    for (i = 0; i < RT51_MAX_TASKS; ++i) {
        expect(rt51_task_get_info(i, &task_info) == RT51_OK && task_info.stack_peak <= RT51_STACK_BYTES, 113);
        sum += task_info.tick_samples;
#if !RT51_ENABLE_STATS
        expect(!task_info.dispatch_count, 114);
#endif
    }
#if RT51_ENABLE_STATS
    expect(sum == kernel_info.ticks, 115);
    sum = 0;
    for (i = 0; i < RT51_MAX_TASKS; ++i) {
        rt51_task_get_info(i, &task_info); sum += task_info.dispatch_count;
    }
    expect(sum == kernel_info.switches, 116);
#else
    expect(!sum && !kernel_info.idle_tick_samples, 117);
#endif
    expect(rt51_task_get_info(255, &task_info) == RT51_INVALID && rt51_task_get_info(0, 0) == RT51_INVALID, 118);
    expect(rt51_kernel_get_info(0) == RT51_INVALID && !EA, 119);
    rt51_critical_exit(ea);
#if RT51_MAX_TIMERS > 0
    expect(isr_checked == 1, 120);
#endif
    finish();
}
static void second(void) { for (;;) rt51_delay(1); }
#endif

void main(void)
{
#if TEST_CASE >= 23 && TEST_CASE <= 25
    uint8_t i;
#endif
    rt51_init();
    rt51_sem_init(&sem, 0, 1); rt51_mutex_init(&mutex);
    rt51_queue_init(&queue, ring, sizeof(ring), 2, 1);
#if TEST_CASE == 17 || TEST_CASE == 23 || TEST_CASE == 24 || TEST_CASE == 25 || TEST_CASE == 30
    rt51_tick_count = 0xfffc;
#endif
    expect(rt51_task_create(first, 1, &ids[0]) == RT51_OK, 131);
    expect(rt51_task_create(second, 5, &ids[1]) == RT51_OK, 132);
    expect(rt51_task_create(third, 10, &ids[2]) == RT51_OK, 133);
#if TEST_CASE >= 23 && TEST_CASE <= 25
    for (i = 0; i < RT51_MAX_TIMERS; ++i) {
#if TEST_CASE == 23
        expect(rt51_timer_init(&timers[i], callback, i == 1 ? &counters[1] : &counters[0], 3, i == 0) == RT51_OK, 134);
#elif TEST_CASE == 24
        expect(rt51_timer_init(&timers[i], callback, &counters[0], 1, i == 0) == RT51_OK, 134);
#else
        expect(rt51_timer_init(&timers[i], callback, i == 1 ? &counters[1] : &counters[0], 1, 0) == RT51_OK, 134);
#endif
    }
    expect(rt51_timer_init(&timers[0], callback, 0, 1, 0) == RT51_INVALID, 135);
    expect(rt51_timer_init(&timers[RT51_MAX_TIMERS], callback, 0, 1, 0) == RT51_FULL, 136);
    expect(rt51_timer_get_info(&timers[RT51_MAX_TIMERS], &timer_info) == RT51_INVALID, 137);
    expect(rt51_timer_dispatch() == RT51_CONTEXT, 138);
    rt51_timer_start(&timers[0], TEST_CASE == 23 ? 2 : 1);
#if TEST_CASE != 24
    rt51_timer_start(&timers[1], TEST_CASE == 23 ? 4 : 1);
#endif
#elif TEST_CASE == 26 && RT51_MAX_TIMERS > 0
    rt51_timer_init(&timers[0], callback, 0, 1, 0);
#elif TEST_CASE == 28 || TEST_CASE == 29
    rt51_timer_init(&timers[0], callback, 0, 1, 0);
    rt51_timer_start(&timers[0], 1);
#elif TEST_CASE == 30
    rt51_timer_init(&timers[0], callback, &counters[0], 1, 0);
    rt51_timer_start(&timers[0], 1);
#endif
    rt51_start(timer_init, timer_ack);
    test_result = 255; EA = 0; for (;;) { }
}
