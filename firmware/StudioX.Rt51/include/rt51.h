#ifndef STUDIOX_RT51_H
#define STUDIOX_RT51_H
#include <stdint.h>
#include "rt51_config.h"

#if !defined(__SDCC_mcs51) || !defined(__SDCC_STACK_AUTO) || !defined(__SDCC_MODEL_LARGE)
#error RT51 requires SDCC MCS51, --model-large and --stack-auto for every C translation unit
#endif

typedef uint8_t rt51_status_t;
typedef void (*rt51_entry_t)(void);
typedef void (*rt51_timer_init_t)(void);
typedef void (*rt51_tick_ack_t)(void);
enum { RT51_OK, RT51_TIMEOUT, RT51_INVALID, RT51_FULL, RT51_CONTEXT, RT51_OWNER, RT51_DEADLOCK, RT51_ABORTED, RT51_BUSY };
enum { RT51_UNUSED, RT51_READY, RT51_SLEEP, RT51_EVENT, RT51_SEMAPHORE, RT51_MUTEX, RT51_QUEUE_SEND, RT51_QUEUE_RECEIVE, RT51_FINISHED, RT51_SUSPENDED };
enum { RT51_FAULT_NONE, RT51_FAULT_STACK, RT51_FAULT_BANK, RT51_FAULT_KERNEL_STACK, RT51_FAULT_LOCK_HELD, RT51_FAULT_TIMER_CALLBACK };
#define RT51_NONE 0xff
#define RT51_FOREVER 0xffffu
#define RT51_NO_WAIT 0u
#define RT51_EVENT_ANY 0
#define RT51_EVENT_ALL 1

typedef struct { uint8_t count, limit; } rt51_sem_t;
typedef struct { uint8_t owner; } rt51_mutex_t;
typedef struct {
    uint8_t __xdata *storage;
    uint8_t item_bytes, capacity, head, tail, count;
} rt51_queue_t;

/* 只读调试快照：共享 16 位字段请先关中断或用 rt51_ticks()；运行中禁止直接改写。 */
typedef struct {
    rt51_entry_t entry;
    void __xdata *object;
    uint8_t __xdata *buffer;
    uint16_t remaining;
    uint8_t state, priority, effective_priority, stack_used, stack_peak;
    uint8_t events, event_mask, event_mode, result, received, locks;
#if RT51_ENABLE_STATS
    uint16_t tick_samples, dispatch_count;
#endif
    uint8_t stack[RT51_STACK_BYTES];
} rt51_task_t;

typedef struct {
    uint8_t state, priority, effective_priority, stack_used, stack_peak, locks;
    uint16_t remaining, tick_samples, dispatch_count;
} rt51_task_info_t;
typedef struct {
    uint16_t ticks, switches, idle_tick_samples;
    uint8_t current, fault, running, kernel_stack_peak;
} rt51_kernel_info_t;

extern __xdata rt51_task_t rt51_tasks[RT51_MAX_TASKS];
extern volatile __data uint8_t rt51_current;
extern volatile __data uint8_t rt51_fault;
extern volatile __data uint8_t rt51_running;
extern volatile __data uint8_t rt51_kernel_stack_peak;
extern volatile __xdata uint16_t rt51_tick_count;
extern volatile __xdata uint16_t rt51_switch_count;

/* 初始化、创建对象/任务仅在启动前进行。数值小的优先级先运行；255 保留给空闲任务。 */
void rt51_init(void);
rt51_status_t rt51_task_create(rt51_entry_t entry, uint8_t priority, uint8_t __xdata *id);
/* 初始化回调配置用户的 tick 定时器/中断，不能打开 EA；成功后不返回。
 * tick_ack 在已保存上下文、EA=0 时清标志/重装，自动清标志的定时器可传 0。
 * tick_ack 必须有界、不可阻塞/启用 EA，使用内核工作栈。 */
rt51_status_t rt51_start(rt51_timer_init_t timer_init, rt51_tick_ack_t tick_ack);
void rt51_yield(void) __naked;
/* 任务上下文、EA=1；单位为板级 tick。delay(0) 主动让出，最大有限等待 65534 tick。 */
rt51_status_t rt51_delay(uint16_t ticks);
/* 固定周期锚点由调用者以 rt51_ticks() 初始化；每次推进 period，不累积执行耗时。
 * period 为 1..32767 tick，相邻调用不能相隔 >=32768 tick。逾期不阻塞并返回 TIMEOUT。
 * 挂起会取消正在进行的延时，恢复后返回 ABORTED；已推进的锚点不回滚。 */
rt51_status_t rt51_delay_until(uint16_t __xdata *anchor, uint16_t period);
/* 仅任务上下文；挂起取消目标的等待，恢复后阻塞 API 返回 ABORTED。
 * 持锁任务返回 OWNER；正在 dispatch 的任务返回 BUSY；闲置/已结束/空闲任务拒绝操作。
 * 重复挂起/恢复返回 BUSY；回调内的任务控制/等待 API 返回 CONTEXT。 */
rt51_status_t rt51_task_suspend(uint8_t task);
rt51_status_t rt51_task_resume(uint8_t task);
/* 原子快照。stack_used/peak 是切换时观测值；tick_samples 不是精确 CPU 用时。
 * 统计关闭时两个任务计数及 idle_tick_samples 为 0。 */
rt51_status_t rt51_task_get_info(uint8_t task, rt51_task_info_t __xdata *info);
rt51_status_t rt51_kernel_get_info(rt51_kernel_info_t __xdata *info);
uint16_t rt51_ticks(void);
uint8_t rt51_critical_enter(void) __naked;
void rt51_critical_exit(uint8_t previous_ea);

rt51_status_t rt51_event_wait(uint8_t mask, uint8_t mode, uint16_t timeout, uint8_t __xdata *received);
rt51_status_t rt51_event_post(uint8_t task, uint8_t flags);
/* FromISR 入口不阻塞、不切换上下文；唤醒任务在下一 tick 获得运行机会。 */
rt51_status_t rt51_event_post_from_isr(uint8_t task, uint8_t flags);
rt51_status_t rt51_sem_init(rt51_sem_t __xdata *sem, uint8_t initial, uint8_t limit);
rt51_status_t rt51_sem_take(rt51_sem_t __xdata *sem, uint16_t timeout);
rt51_status_t rt51_sem_give(rt51_sem_t __xdata *sem);
rt51_status_t rt51_sem_give_from_isr(rt51_sem_t __xdata *sem);
rt51_status_t rt51_mutex_init(rt51_mutex_t __xdata *mutex);
rt51_status_t rt51_mutex_lock(rt51_mutex_t __xdata *mutex, uint16_t timeout);
rt51_status_t rt51_mutex_unlock(rt51_mutex_t __xdata *mutex);
/* 队列/缓冲区必须位于 XRAM 且在等待期间有效；单条最多 16 字节，消息按值复制。 */
rt51_status_t rt51_queue_init(rt51_queue_t __xdata *queue, uint8_t __xdata *storage,
    uint16_t storage_bytes, uint8_t capacity, uint8_t item_bytes);
rt51_status_t rt51_queue_send(rt51_queue_t __xdata *queue, const uint8_t __xdata *item, uint16_t timeout);
rt51_status_t rt51_queue_receive(rt51_queue_t __xdata *queue, uint8_t __xdata *item, uint16_t timeout);
rt51_status_t rt51_queue_send_from_isr(rt51_queue_t __xdata *queue, const uint8_t __xdata *item);

#if RT51_MAX_TIMERS > 0
typedef void (*rt51_timer_callback_t)(void __xdata *context);
typedef struct {
    rt51_timer_callback_t callback;
    void __xdata *context;
    uint16_t period, remaining, overruns;
    uint8_t repeat, active, pending;
} rt51_timer_t;
typedef struct {
    uint16_t remaining, overruns;
    uint8_t active, pending;
} rt51_timer_info_t;
/* 对象及 context 放 XRAM 并保持整个运行期有效；仅启动前注册，不支持删除/重建。
 * period 为 1..65534，repeat 为 0/1；init 后停止，start 的 delay 是首次到期时间。
 * start/stop 可在启动前或任务调用；清除未处理的到期次数，不中止已开始的回调。
 * 周期从实际到期 tick 推进，pending 饱和 255；额外到期计入饱和 overruns。
 * dispatch 每次每对象最多调用一次，在调用任务、EA=1 下执行；回调不得阻塞。
 * 回调中的任务等待/控制 API 返回 CONTEXT；若回调返回时 EA=0，保留故障码 5 停住。
 * 回调可 start/stop 定时器；嵌套或并发 dispatch 返回 BUSY，不会重入回调。 */
rt51_status_t rt51_timer_init(rt51_timer_t __xdata *timer, rt51_timer_callback_t callback,
    void __xdata *context, uint16_t period, uint8_t repeat);
rt51_status_t rt51_timer_start(rt51_timer_t __xdata *timer, uint16_t delay);
rt51_status_t rt51_timer_stop(rt51_timer_t __xdata *timer);
rt51_status_t rt51_timer_dispatch(void);
rt51_status_t rt51_timer_get_info(rt51_timer_t __xdata *timer, rt51_timer_info_t __xdata *info);
#endif

/* 在 main.c 展开一次，向量编号由用户选择。该 ISR 使用 RETI 恢复下一个任务，
 * 因此不可从普通 C ISR 调用，也不可在跳转前添加 C 代码/编译器 ISR 序言。 */
void rt51_port_tick(void) __naked;
#define RT51_TICK_ISR(name, vector) \
    void name(void) __interrupt(vector) __naked { __asm ljmp _rt51_port_tick __endasm; }
#endif
