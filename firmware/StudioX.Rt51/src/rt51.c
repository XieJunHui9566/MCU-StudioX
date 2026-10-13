#include "rt51_internal.h"

__xdata rt51_task_t rt51_tasks[RT51_MAX_TASKS];
volatile __data uint8_t rt51_current, rt51_fault, rt51_running;
volatile __data uint8_t rt51_kernel_stack_peak;
volatile __xdata uint16_t rt51_tick_count, rt51_switch_count;
/* --idata-loc 0x30 把此区放在 30..7F；硬件任务栈紧接于 80..FF。 */
__idata uint8_t rt51_kernel_stack[RT51_KERNEL_STACK_BYTES];
__data uint8_t rt51_port_kind, rt51_saved_sp, rt51_from_len, rt51_to_len;
uint8_t __xdata * __data rt51_from;
uint8_t __xdata * __data rt51_to;
static __xdata rt51_tick_ack_t acknowledge_tick;
static void inherit(void);

void rt51_trap(uint8_t fault)
{
    EA = 0;
    rt51_fault = fault;
    for (;;) { }
}

static void idle(void) { for (;;) { } }

static void prepare(uint8_t id, rt51_entry_t entry, uint8_t priority)
{
    uint8_t j;
    rt51_task_t __xdata *t = &rt51_tasks[id];
    t->entry = entry;
    t->object = 0;
    t->buffer = 0;
    t->remaining = 0;
    t->state = RT51_READY;
    t->priority = t->effective_priority = priority;
    t->events = t->event_mask = t->event_mode = t->received = t->locks = 0;
    t->result = RT51_OK;
#if RT51_ENABLE_STATS
    t->tick_samples = t->dispatch_count = 0;
#endif
    for (j = 0; j < RT51_STACK_BYTES; ++j) t->stack[j] = 0;
    /* PC(2), IE, ACC/B/DPTR/PSW(5), R0..R7(8), _bp, bits，共 18 字节。 */
    t->stack[0] = (uint16_t)rt51_bootstrap;
    t->stack[1] = (uint16_t)rt51_bootstrap >> 8;
    t->stack[2] = IE | 0x80;
    t->stack_used = t->stack_peak = 18;
}

void rt51_init(void)
{
    uint8_t i;
    EA = 0;
    rt51_current = RT51_NONE;
    rt51_fault = rt51_running = 0;
    rt51_kernel_stack_peak = 0;
    for (i = 0; i < RT51_KERNEL_STACK_BYTES; ++i) rt51_kernel_stack[i] = 0xa5;
    rt51_tick_count = rt51_switch_count = 0;
    for (i = 0; i < RT51_MAX_TASKS; ++i) rt51_tasks[i].state = RT51_UNUSED;
#if RT51_MAX_TIMERS > 0
    rt51_timers_reset();
#endif
}

rt51_status_t rt51_task_create(rt51_entry_t entry, uint8_t priority, uint8_t __xdata *id)
{
    uint8_t i;
    if (rt51_running || !entry || !id || priority == 255) return RT51_INVALID;
    for (i = 0; i < RT51_MAX_TASKS - 1; ++i)
        if (rt51_tasks[i].state == RT51_UNUSED) {
            prepare(i, entry, priority);
            *id = i;
            return RT51_OK;
        }
    return RT51_FULL;
}

rt51_status_t rt51_start(rt51_timer_init_t timer_init, rt51_tick_ack_t tick_ack)
{
    uint8_t i;
    if (rt51_running || !timer_init) return RT51_INVALID;
    EA = 0;
    acknowledge_tick = tick_ack;
    timer_init();
    if (EA) { EA = 0; return RT51_CONTEXT; }
    for (i = 0; i < RT51_MAX_TASKS - 1; ++i)
        if (rt51_tasks[i].state == RT51_READY) rt51_tasks[i].stack[2] = IE | 0x80;
    prepare(RT51_MAX_TASKS - 1, idle, 255);
    rt51_running = 1;
    rt51_port_start();
    return RT51_CONTEXT;
}

/* 调度器只在独立工作栈上运行，所有循环次数由 RT51_MAX_TASKS 限定。 */
void rt51_select(void)
{
    uint8_t i, n, next, best = 255, previous = rt51_current, update_inheritance = 0;
    rt51_task_t __xdata *t;
    if (rt51_port_kind == 1) {
        if (acknowledge_tick) acknowledge_tick();
        if (EA) rt51_trap(RT51_FAULT_KERNEL_STACK);
        ++rt51_tick_count;
#if RT51_ENABLE_STATS
        if (previous != RT51_NONE) ++rt51_tasks[previous].tick_samples;
#endif
#if RT51_MAX_TIMERS > 0
        rt51_timers_tick();
#endif
        for (i = 0; i < RT51_MAX_TASKS - 1; ++i) {
            t = &rt51_tasks[i];
            if (t->state >= RT51_SLEEP && t->state <= RT51_QUEUE_RECEIVE && t->remaining != RT51_FOREVER) {
                if (t->remaining && !--t->remaining) {
                    if (t->state == RT51_MUTEX) update_inheritance = 1;
                    t->result = t->state == RT51_SLEEP ? RT51_OK : RT51_TIMEOUT;
                    t->state = RT51_READY;
                }
            }
        }
        if (update_inheritance) inherit();
    }
    if (previous != RT51_NONE) {
        if (rt51_saved_sp < RT51_STACK_BASE || rt51_saved_sp - (RT51_STACK_BASE - 1) > RT51_STACK_BYTES)
            rt51_trap(RT51_FAULT_STACK);
        t = &rt51_tasks[previous];
        t->stack_used = rt51_saved_sp - (RT51_STACK_BASE - 1);
        if (t->stack_peak < t->stack_used) t->stack_peak = t->stack_used;
    }
    next = RT51_MAX_TASKS - 1;
    i = previous == RT51_NONE ? RT51_MAX_TASKS - 1 : previous;
    for (n = 0; n < RT51_MAX_TASKS; ++n) {
        if (++i == RT51_MAX_TASKS) i = 0;
        t = &rt51_tasks[i];
        if (t->state == RT51_READY && (t->effective_priority < best || next == RT51_MAX_TASKS - 1)) {
            best = t->effective_priority;
            next = i;
        }
    }
    rt51_from_len = 0;
    rt51_to_len = 0;
    if (previous == next) return;
    if (previous != RT51_NONE) {
        rt51_from = rt51_tasks[previous].stack;
        rt51_from_len = rt51_tasks[previous].stack_used;
    }
    rt51_to = rt51_tasks[next].stack;
    rt51_to_len = rt51_tasks[next].stack_used;
    rt51_current = next;
    ++rt51_switch_count;
#if RT51_ENABLE_STATS
    ++rt51_tasks[next].dispatch_count;
#endif
}

void rt51_bootstrap(void)
{
    rt51_tasks[rt51_current].entry();
    EA = 0;
    if (rt51_tasks[rt51_current].locks) rt51_trap(RT51_FAULT_LOCK_HELD);
    rt51_tasks[rt51_current].state = RT51_FINISHED;
    rt51_yield();
    rt51_trap(RT51_FAULT_STACK);
}

void rt51_critical_exit(uint8_t previous_ea) { if (previous_ea) EA = 1; }
uint16_t rt51_ticks(void)
{
    uint8_t ea = rt51_critical_enter();
    uint16_t value = rt51_tick_count;
    rt51_critical_exit(ea);
    return value;
}

static uint8_t task_context(uint8_t ea)
{
#if RT51_MAX_TIMERS > 0
    if (rt51_timer_dispatch_owner(rt51_current)) return 0;
#endif
    return ea && rt51_running && rt51_current < RT51_MAX_TASKS - 1;
}

rt51_status_t rt51_delay(uint16_t ticks)
{
    uint8_t ea = rt51_critical_enter();
    rt51_status_t status;
    if (!task_context(ea) || ticks == RT51_FOREVER) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (ticks) {
        rt51_tasks[rt51_current].remaining = ticks;
        rt51_tasks[rt51_current].state = RT51_SLEEP;
        rt51_tasks[rt51_current].result = RT51_OK;
    }
    rt51_yield();
    status = ticks ? rt51_tasks[rt51_current].result : RT51_OK;
    rt51_critical_exit(ea);
    return status;
}

rt51_status_t rt51_delay_until(uint16_t __xdata *anchor, uint16_t period)
{
    uint8_t ea = rt51_critical_enter();
    uint16_t remaining;
    rt51_status_t status = RT51_OK;
    if (!task_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (!anchor || !period || period > 32767u) { rt51_critical_exit(ea); return RT51_INVALID; }
    *anchor += period;
    remaining = *anchor - rt51_tick_count;
    if (remaining && remaining <= 32767u) {
        rt51_tasks[rt51_current].remaining = remaining;
        rt51_tasks[rt51_current].state = RT51_SLEEP;
        rt51_tasks[rt51_current].result = RT51_OK;
        rt51_yield();
        status = rt51_tasks[rt51_current].result;
    }
    else if (remaining) status = RT51_TIMEOUT;
    rt51_critical_exit(ea);
    return status;
}

rt51_status_t rt51_task_suspend(uint8_t task)
{
    uint8_t ea = rt51_critical_enter(), was_mutex;
    rt51_task_t __xdata *t;
    if (!task_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (task >= RT51_MAX_TASKS - 1 || rt51_tasks[task].state == RT51_UNUSED || rt51_tasks[task].state == RT51_FINISHED) {
        rt51_critical_exit(ea); return RT51_INVALID;
    }
    t = &rt51_tasks[task];
    if (t->state == RT51_SUSPENDED) { rt51_critical_exit(ea); return RT51_BUSY; }
#if RT51_MAX_TIMERS > 0
    if (rt51_timer_dispatch_owner(task)) { rt51_critical_exit(ea); return RT51_BUSY; }
#endif
    if (t->locks) { rt51_critical_exit(ea); return RT51_OWNER; }
    was_mutex = t->state == RT51_MUTEX;
    /* READY 可能已经领取了信号量许可/队列消息；不能把已完成的等待改成取消。 */
    if (t->state >= RT51_SLEEP && t->state <= RT51_QUEUE_RECEIVE) t->result = RT51_ABORTED;
    t->remaining = 0;
    t->object = 0;
    t->buffer = 0;
    t->state = RT51_SUSPENDED;
    if (was_mutex) inherit();
    if (task == rt51_current) rt51_yield();
    rt51_critical_exit(ea);
    return RT51_OK;
}

rt51_status_t rt51_task_resume(uint8_t task)
{
    uint8_t ea = rt51_critical_enter();
    if (!task_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (task >= RT51_MAX_TASKS - 1 || rt51_tasks[task].state == RT51_UNUSED || rt51_tasks[task].state == RT51_FINISHED) {
        rt51_critical_exit(ea); return RT51_INVALID;
    }
    if (rt51_tasks[task].state != RT51_SUSPENDED) { rt51_critical_exit(ea); return RT51_BUSY; }
    rt51_tasks[task].state = RT51_READY;
    rt51_yield();
    rt51_critical_exit(ea);
    return RT51_OK;
}

rt51_status_t rt51_task_get_info(uint8_t task, rt51_task_info_t __xdata *info)
{
    uint8_t ea = rt51_critical_enter();
    rt51_task_t __xdata *t;
    if (!info || task >= RT51_MAX_TASKS || rt51_tasks[task].state == RT51_UNUSED) {
        rt51_critical_exit(ea); return RT51_INVALID;
    }
    t = &rt51_tasks[task];
    info->state = t->state; info->priority = t->priority; info->effective_priority = t->effective_priority;
    info->stack_used = t->stack_used; info->stack_peak = t->stack_peak; info->locks = t->locks;
    info->remaining = t->remaining;
#if RT51_ENABLE_STATS
    info->tick_samples = t->tick_samples; info->dispatch_count = t->dispatch_count;
#else
    info->tick_samples = info->dispatch_count = 0;
#endif
    rt51_critical_exit(ea); return RT51_OK;
}

rt51_status_t rt51_kernel_get_info(rt51_kernel_info_t __xdata *info)
{
    uint8_t ea = rt51_critical_enter();
    if (!info) { rt51_critical_exit(ea); return RT51_INVALID; }
    info->ticks = rt51_tick_count; info->switches = rt51_switch_count;
    info->current = rt51_current; info->fault = rt51_fault; info->running = rt51_running;
    info->kernel_stack_peak = rt51_kernel_stack_peak;
#if RT51_ENABLE_STATS
    info->idle_tick_samples = rt51_running ? rt51_tasks[RT51_MAX_TASKS - 1].tick_samples : 0;
#else
    info->idle_tick_samples = 0;
#endif
    rt51_critical_exit(ea); return RT51_OK;
}

/* 调用者已关中断；等待结果存在 TCB，恢复后读取自身结果。 */
static rt51_status_t wait(uint8_t state, void __xdata *object, uint16_t timeout)
{
    rt51_task_t __xdata *t = &rt51_tasks[rt51_current];
    if (!timeout) return RT51_TIMEOUT;
    t->state = state;
    t->object = object;
    t->remaining = timeout;
    t->result = RT51_TIMEOUT;
    rt51_yield();
    return rt51_tasks[rt51_current].result;
}

static uint8_t waiter(uint8_t state, void __xdata *object)
{
    uint8_t i, found = RT51_NONE, priority = 255;
    for (i = 0; i < RT51_MAX_TASKS - 1; ++i)
        if (rt51_tasks[i].state == state && rt51_tasks[i].object == object &&
            (found == RT51_NONE || rt51_tasks[i].effective_priority < priority)) {
            found = i;
            priority = rt51_tasks[i].effective_priority;
        }
    return found;
}

static void wake(uint8_t id)
{
    rt51_tasks[id].result = RT51_OK;
    rt51_tasks[id].state = RT51_READY;
}

static uint8_t event_match(rt51_task_t __xdata *t)
{
    uint8_t match = t->events & t->event_mask;
    return t->event_mode == RT51_EVENT_ALL && match != t->event_mask ? 0 : match;
}

rt51_status_t rt51_event_wait(uint8_t mask, uint8_t mode, uint16_t timeout, uint8_t __xdata *received)
{
    uint8_t ea = rt51_critical_enter(), match;
    rt51_status_t status;
    rt51_task_t __xdata *t = &rt51_tasks[rt51_current];
    if (!task_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (!mask || mode > RT51_EVENT_ALL || !received) { rt51_critical_exit(ea); return RT51_INVALID; }
    t->event_mask = mask; t->event_mode = mode; t->received = 0;
    match = event_match(t);
    if (match) { t->events &= ~match; t->received = match; status = RT51_OK; }
    else status = wait(RT51_EVENT, 0, timeout);
    *received = rt51_tasks[rt51_current].received;
    rt51_critical_exit(ea);
    return status;
}

rt51_status_t rt51_event_post_from_isr(uint8_t task, uint8_t flags)
{
    uint8_t ea = rt51_critical_enter(), match;
    rt51_task_t __xdata *t;
    if (task >= RT51_MAX_TASKS - 1 || !flags || rt51_tasks[task].state == RT51_UNUSED || rt51_tasks[task].state == RT51_FINISHED) {
        rt51_critical_exit(ea); return RT51_INVALID;
    }
    t = &rt51_tasks[task]; t->events |= flags;
    if (t->state == RT51_EVENT && (match = event_match(t))) {
        t->events &= ~match; t->received = match; wake(task);
    }
    rt51_critical_exit(ea); return RT51_OK;
}
rt51_status_t rt51_event_post(uint8_t task, uint8_t flags)
{
    rt51_status_t s = rt51_event_post_from_isr(task, flags);
    if (s == RT51_OK && rt51_running && EA) rt51_yield();
    return s;
}

rt51_status_t rt51_sem_init(rt51_sem_t __xdata *sem, uint8_t initial, uint8_t limit)
{
    if (rt51_running || !sem || !limit || initial > limit) return RT51_INVALID;
    sem->count = initial; sem->limit = limit; return RT51_OK;
}
rt51_status_t rt51_sem_take(rt51_sem_t __xdata *sem, uint16_t timeout)
{
    uint8_t ea = rt51_critical_enter(); rt51_status_t s;
    if (!task_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (!sem || !sem->limit) { rt51_critical_exit(ea); return RT51_INVALID; }
    if (sem->count) { --sem->count; s = RT51_OK; }
    else s = wait(RT51_SEMAPHORE, sem, timeout);
    rt51_critical_exit(ea); return s;
}
rt51_status_t rt51_sem_give_from_isr(rt51_sem_t __xdata *sem)
{
    uint8_t ea = rt51_critical_enter(), id; rt51_status_t s = RT51_OK;
    if (!sem || !sem->limit) s = RT51_INVALID;
    else if ((id = waiter(RT51_SEMAPHORE, sem)) != RT51_NONE) wake(id);
    else if (sem->count == sem->limit) s = RT51_FULL;
    else ++sem->count;
    rt51_critical_exit(ea); return s;
}
rt51_status_t rt51_sem_give(rt51_sem_t __xdata *sem)
{
    rt51_status_t s = rt51_sem_give_from_isr(sem);
    if (s == RT51_OK && rt51_running && EA) rt51_yield();
    return s;
}

/* 有界传播支持嵌套互斥锁；每轮只可能把优先级提升，最多 N 轮收敛。 */
static void inherit(void)
{
    uint8_t i, round, owner;
    rt51_mutex_t __xdata *m;
    for (i = 0; i < RT51_MAX_TASKS; ++i) rt51_tasks[i].effective_priority = rt51_tasks[i].priority;
    for (round = 0; round < RT51_MAX_TASKS; ++round)
        for (i = 0; i < RT51_MAX_TASKS - 1; ++i)
            if (rt51_tasks[i].state == RT51_MUTEX) {
                m = (rt51_mutex_t __xdata *)rt51_tasks[i].object;
                owner = m->owner;
                if (owner < RT51_MAX_TASKS - 1 && rt51_tasks[owner].effective_priority > rt51_tasks[i].effective_priority)
                    rt51_tasks[owner].effective_priority = rt51_tasks[i].effective_priority;
            }
}
rt51_status_t rt51_mutex_init(rt51_mutex_t __xdata *mutex)
{
    if (rt51_running || !mutex) return RT51_INVALID;
    mutex->owner = RT51_NONE; return RT51_OK;
}
rt51_status_t rt51_mutex_lock(rt51_mutex_t __xdata *mutex, uint16_t timeout)
{
    uint8_t ea = rt51_critical_enter(), owner, n;
    rt51_status_t s;
    rt51_mutex_t __xdata *m;
    if (!task_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (!mutex) { rt51_critical_exit(ea); return RT51_INVALID; }
    if (rt51_tasks[rt51_current].locks == 255) { rt51_critical_exit(ea); return RT51_FULL; }
    if (mutex->owner == RT51_NONE) { mutex->owner = rt51_current; ++rt51_tasks[rt51_current].locks; s = RT51_OK; }
    else {
        owner = mutex->owner;
        for (n = 0; n < RT51_MAX_TASKS; ++n) {
            if (owner == rt51_current) { rt51_critical_exit(ea); return RT51_DEADLOCK; }
            if (owner >= RT51_MAX_TASKS - 1) { rt51_critical_exit(ea); return RT51_INVALID; }
            if (rt51_tasks[owner].state != RT51_MUTEX) break;
            m = (rt51_mutex_t __xdata *)rt51_tasks[owner].object; owner = m->owner;
        }
        if (!timeout) s = RT51_TIMEOUT;
        else {
            rt51_tasks[rt51_current].state = RT51_MUTEX;
            rt51_tasks[rt51_current].object = mutex;
            rt51_tasks[rt51_current].remaining = timeout;
            rt51_tasks[rt51_current].result = RT51_TIMEOUT;
            inherit(); rt51_yield();
            s = rt51_tasks[rt51_current].result; inherit();
        }
    }
    rt51_critical_exit(ea); return s;
}
rt51_status_t rt51_mutex_unlock(rt51_mutex_t __xdata *mutex)
{
    uint8_t ea = rt51_critical_enter(), id;
    if (!task_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (!mutex || mutex->owner != rt51_current) { rt51_critical_exit(ea); return RT51_OWNER; }
    --rt51_tasks[rt51_current].locks;
    id = waiter(RT51_MUTEX, mutex); mutex->owner = id;
    if (id != RT51_NONE) { ++rt51_tasks[id].locks; wake(id); }
    inherit(); rt51_yield(); rt51_critical_exit(ea); return RT51_OK;
}

static void copy(uint8_t __xdata *to, const uint8_t __xdata *from, uint8_t bytes)
{
    while (bytes--) *to++ = *from++;
}
rt51_status_t rt51_queue_init(rt51_queue_t __xdata *q, uint8_t __xdata *storage,
    uint16_t storage_bytes, uint8_t capacity, uint8_t item_bytes)
{
    if (rt51_running || !q || !storage || !capacity || !item_bytes || item_bytes > RT51_QUEUE_ITEM_MAX ||
        (uint16_t)capacity * item_bytes > storage_bytes) return RT51_INVALID;
    q->storage = storage; q->item_bytes = item_bytes; q->capacity = capacity;
    q->head = q->tail = q->count = 0; return RT51_OK;
}
static void enqueue(rt51_queue_t __xdata *q, const uint8_t __xdata *item)
{
    copy(q->storage + (uint16_t)q->tail * q->item_bytes, item, q->item_bytes);
    if (++q->tail == q->capacity) q->tail = 0;
    ++q->count;
}
rt51_status_t rt51_queue_send_from_isr(rt51_queue_t __xdata *q, const uint8_t __xdata *item)
{
    uint8_t ea = rt51_critical_enter(), id; rt51_status_t s = RT51_OK;
    if (!q || !item || !q->capacity || !q->item_bytes || q->item_bytes > RT51_QUEUE_ITEM_MAX) s = RT51_INVALID;
    else if ((id = waiter(RT51_QUEUE_RECEIVE, q)) != RT51_NONE) {
        copy(rt51_tasks[id].buffer, item, q->item_bytes); wake(id);
    }
    else if (q->count == q->capacity) s = RT51_FULL;
    else enqueue(q, item);
    rt51_critical_exit(ea); return s;
}
rt51_status_t rt51_queue_send(rt51_queue_t __xdata *q, const uint8_t __xdata *item, uint16_t timeout)
{
    uint8_t ea = rt51_critical_enter(); rt51_status_t s;
    if (!task_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    s = rt51_queue_send_from_isr(q, item);
    if (s == RT51_FULL && timeout) {
        rt51_tasks[rt51_current].buffer = (uint8_t __xdata *)item;
        s = wait(RT51_QUEUE_SEND, q, timeout);
    }
    else if (s == RT51_OK) rt51_yield();
    rt51_critical_exit(ea); return s;
}
rt51_status_t rt51_queue_receive(rt51_queue_t __xdata *q, uint8_t __xdata *item, uint16_t timeout)
{
    uint8_t ea = rt51_critical_enter(), id; rt51_status_t s;
    if (!task_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (!q || !item || !q->capacity || !q->item_bytes || q->item_bytes > RT51_QUEUE_ITEM_MAX) {
        rt51_critical_exit(ea); return RT51_INVALID;
    }
    if (q->count) {
        copy(item, q->storage + (uint16_t)q->head * q->item_bytes, q->item_bytes);
        if (++q->head == q->capacity) q->head = 0;
        --q->count;
        if ((id = waiter(RT51_QUEUE_SEND, q)) != RT51_NONE) { enqueue(q, rt51_tasks[id].buffer); wake(id); }
        s = RT51_OK; rt51_yield();
    }
    else { rt51_tasks[rt51_current].buffer = item; s = wait(RT51_QUEUE_RECEIVE, q, timeout); }
    rt51_critical_exit(ea); return s;
}
