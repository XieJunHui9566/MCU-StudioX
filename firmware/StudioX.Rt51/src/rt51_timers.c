#include "rt51_internal.h"

#if RT51_MAX_TIMERS > 0
/* 只存静态对象指针；不额外占用任务槽，不在 tick ISR 的工作栈上调用用户函数。 */
static rt51_timer_t __xdata * __xdata timers[RT51_MAX_TIMERS];
static __xdata uint8_t dispatch_busy;
static __xdata uint8_t dispatch_task;

void rt51_timers_reset(void)
{
    uint8_t i;
    for (i = 0; i < RT51_MAX_TIMERS; ++i) timers[i] = 0;
    dispatch_busy = 0;
    dispatch_task = RT51_NONE;
}

uint8_t rt51_timer_dispatch_owner(uint8_t task)
{
    return dispatch_busy && dispatch_task == task;
}

static uint8_t registered(rt51_timer_t __xdata *timer)
{
    uint8_t i;
    if (timer) for (i = 0; i < RT51_MAX_TIMERS; ++i) if (timers[i] == timer) return 1;
    return 0;
}

void rt51_timers_tick(void)
{
    uint8_t i;
    rt51_timer_t __xdata *t;
    for (i = 0; i < RT51_MAX_TIMERS; ++i) {
        t = timers[i];
        if (t && t->active && !--t->remaining) {
            if (t->pending < 255) ++t->pending;
            else if (t->overruns < 65535u) ++t->overruns;
            if (t->repeat) t->remaining = t->period;
            else t->active = 0;
        }
    }
}

rt51_status_t rt51_timer_init(rt51_timer_t __xdata *timer, rt51_timer_callback_t callback,
    void __xdata *context, uint16_t period, uint8_t repeat)
{
    uint8_t i;
    if (rt51_running || !timer || !callback || !period || period == RT51_FOREVER || repeat > 1 || registered(timer)) return RT51_INVALID;
    for (i = 0; i < RT51_MAX_TIMERS; ++i) if (!timers[i]) {
        timer->callback = callback; timer->context = context; timer->period = period; timer->repeat = repeat;
        timer->active = timer->pending = 0; timer->remaining = timer->overruns = 0;
        timers[i] = timer; return RT51_OK;
    }
    return RT51_FULL;
}

static uint8_t control_context(uint8_t ea)
{
    return !rt51_running || (ea && rt51_current < RT51_MAX_TASKS - 1);
}

rt51_status_t rt51_timer_start(rt51_timer_t __xdata *timer, uint16_t delay)
{
    uint8_t ea = rt51_critical_enter();
    if (!control_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (!registered(timer) || !delay || delay == RT51_FOREVER) { rt51_critical_exit(ea); return RT51_INVALID; }
    timer->remaining = delay; timer->pending = 0; timer->active = 1;
    rt51_critical_exit(ea); return RT51_OK;
}

rt51_status_t rt51_timer_stop(rt51_timer_t __xdata *timer)
{
    uint8_t ea = rt51_critical_enter();
    if (!control_context(ea)) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (!registered(timer)) { rt51_critical_exit(ea); return RT51_INVALID; }
    timer->active = timer->pending = 0; timer->remaining = 0;
    rt51_critical_exit(ea); return RT51_OK;
}

rt51_status_t rt51_timer_dispatch(void)
{
    uint8_t ea = rt51_critical_enter(), i;
    rt51_timer_t __xdata *t;
    rt51_timer_callback_t callback;
    void __xdata *context;
    if (!ea || !rt51_running || rt51_current >= RT51_MAX_TASKS - 1) { rt51_critical_exit(ea); return RT51_CONTEXT; }
    if (dispatch_busy) { rt51_critical_exit(ea); return RT51_BUSY; }
    dispatch_busy = 1;
    dispatch_task = rt51_current;
    rt51_critical_exit(ea);
    for (i = 0; i < RT51_MAX_TIMERS; ++i) {
        ea = rt51_critical_enter();
        t = timers[i]; callback = 0; context = 0;
        if (t && t->pending) { --t->pending; callback = t->callback; context = t->context; }
        rt51_critical_exit(ea);
        if (callback) {
            callback(context);
            if (!EA) rt51_trap(RT51_FAULT_TIMER_CALLBACK);
        }
    }
    ea = rt51_critical_enter(); dispatch_busy = 0; dispatch_task = RT51_NONE; rt51_critical_exit(ea);
    return RT51_OK;
}

rt51_status_t rt51_timer_get_info(rt51_timer_t __xdata *timer, rt51_timer_info_t __xdata *info)
{
    uint8_t ea = rt51_critical_enter();
    if (!info || !registered(timer)) { rt51_critical_exit(ea); return RT51_INVALID; }
    info->remaining = timer->remaining; info->overruns = timer->overruns;
    info->active = timer->active; info->pending = timer->pending;
    rt51_critical_exit(ea); return RT51_OK;
}
#endif
