#include "stc15.h"
#include "rt51.h"

#ifndef STUDIOX_CLOCK_HZ
#error Set the exact MCU clock frequency in the project preset
#endif
#if STUDIOX_CLOCK_HZ < 1000000UL || STUDIOX_CLOCK_HZ > 35000000UL
#error This example needs a verified STC15 clock between 1 and 35MHz
#endif
#define TICK_COUNTS ((STUDIOX_CLOCK_HZ + 500UL) / 1000UL)
#define TICK_RELOAD (65536UL - TICK_COUNTS)

/* 时间基准由应用提供：约 1ms，计数取最接近整数；12MHz 时为精确 1ms。
 * IAP15 Timer0 模式 0 为 16 位自动重装，AUXR.7 选择 1T。
 * 不触碰 Mon51 使用的 P3.0/P3.1；换板时应替换本回调与向量绑定。 */
static void timer_init(void)
{
    TR0 = 0;
    AUXR |= 0x80;
    TMOD &= 0xf0;
    TL0 = (uint8_t)TICK_RELOAD;
    TH0 = (uint8_t)(TICK_RELOAD >> 8);
    TF0 = 0;
    ET0 = 1;
    TR0 = 1;
}
RT51_TICK_ISR(system_tick, 1)

volatile __xdata uint16_t producer_count, consumer_count, heartbeat, timer_heartbeat, period_misses;
volatile __xdata rt51_status_t app_status;
static __xdata uint8_t task_id, message, received, queue_storage[4];
static __xdata rt51_queue_t messages;
static __xdata uint16_t next_release;
#if RT51_MAX_TIMERS > 0
static __xdata rt51_timer_t heartbeat_timer;
static void on_heartbeat(void __xdata *context)
{
    context;
    /* 回调在 producer 任务中执行，不在 tick ISR 中执行，不调用等待 API。 */
    ++timer_heartbeat;
}
#endif

static void producer(void)
{
    rt51_status_t status;
    next_release = rt51_ticks();
    for (;;) {
        message = (uint8_t)++producer_count;
        status = rt51_queue_send(&messages, &message, RT51_FOREVER);
        if (status != RT51_OK) { app_status = status; return; }
#if RT51_MAX_TIMERS > 0
        status = rt51_timer_dispatch();
        if (status != RT51_OK) { app_status = status; return; }
#endif
        status = rt51_delay_until(&next_release, 100);
        if (status == RT51_TIMEOUT) ++period_misses;
        else if (status != RT51_OK) { app_status = status; return; }
    }
}
static void consumer(void)
{
    rt51_status_t status;
    for (;;) {
        status = rt51_queue_receive(&messages, &received, RT51_FOREVER);
        if (status != RT51_OK) { app_status = status; return; }
        ++consumer_count;
    }
}
static void background(void)
{
    /* 不调用 yield 的后台任务用于观察真实 tick 抢占。无板级引脚假设。 */
    for (;;) ++heartbeat;
}
void main(void)
{
    rt51_init();
    app_status = rt51_queue_init(&messages, queue_storage, sizeof(queue_storage), 4, 1);
    if (app_status) goto halted;
#if RT51_MAX_TIMERS > 0
    app_status = rt51_timer_init(&heartbeat_timer, on_heartbeat, 0, 1000, 1);
    if (app_status) goto halted;
    app_status = rt51_timer_start(&heartbeat_timer, 1000);
    if (app_status) goto halted;
#endif
    app_status = rt51_task_create(consumer, 1, &task_id);
    if (app_status) goto halted;
    app_status = rt51_task_create(producer, 2, &task_id);
    if (app_status) goto halted;
    app_status = rt51_task_create(background, 10, &task_id);
    if (app_status) goto halted;
    app_status = rt51_start(timer_init, 0);
halted:
    /* 裸机 main 没有可返回的调用者；初始化错误保留状态并停住。 */
    EA = 0;
    for (;;) { }
}
