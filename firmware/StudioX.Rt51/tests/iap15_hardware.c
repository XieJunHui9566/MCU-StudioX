#include "stc15.h"
#include "rt51.h"

#if STUDIOX_CLOCK_HZ != 12000000UL
#error The board acceptance image is scoped to an internal 12MHz clock
#endif
#if defined(STUDIOX_MON51_DEBUG)
#error This standalone UART acceptance image uses P3.0/P3.1 and cannot run under Mon51
#endif

/* CT107D V3.1 的 CH340C 接 UART1 默认引脚。仅测试固件使用这两个引脚；
 * 不驱动 P0/P2 锁存总线、继电器、电机、蜂鸣器或 EEPROM。 */
volatile __xdata uint16_t acceptance_flags, consumed, produced, shared_value;
volatile __xdata uint16_t arithmetic_errors, isr_pulses, isr_wakes, timeouts, uart_timeouts;
volatile __xdata uint32_t hog_iterations;
volatile __xdata uint16_t math_iterations;
static volatile __xdata uint32_t known_dividend=0xfedcba98UL;
volatile __xdata uint32_t boot_dividend, boot_quotient, boot_remainder;
volatile __xdata uint8_t acceptance_failure, phase, reports;
static __xdata uint8_t ids[3], sent[3], received[3], storage[6], event_bits, wire[80];
static __xdata rt51_queue_t queue;
static __xdata rt51_sem_t pulse;
static __xdata rt51_mutex_t mutex;
static uint8_t __data isr_divider;
static volatile __xdata uint8_t low_owns_mutex;
static __xdata uint16_t next_release;
static __xdata rt51_timer_t periodic_timer, one_shot_timer;
static __xdata rt51_task_info_t task_info;
static __xdata rt51_kernel_info_t kernel_info;
static __xdata rt51_timer_info_t timer_info;
volatile __xdata uint16_t timer_callbacks, one_shot_callbacks, period_misses, suspend_checks;
volatile __xdata uint16_t tick_sample_sum, dispatch_sample_sum, snapshot_checks;
volatile __xdata uint8_t callback_context_errors;

enum { CHECK_QUEUE=1, CHECK_EVENT=2, CHECK_SEM=4, CHECK_DELAY=8,
       CHECK_INHERIT=16, CHECK_PREEMPT=32, CHECK_MATH=64, CHECK_ISR=128,
       CHECK_TIMEOUT=256, CHECK_CRITICAL=512, CHECK_WRAP=1024,
       CHECK_PERIOD=2048, CHECK_SUSPEND=4096, CHECK_TIMER=8192,
       CHECK_SNAPSHOT=16384 };
#define CHECK_CALLBACK 32768u

static void timer_init(void)
{
#if defined(RT51_ACCEPTANCE_SIMULATOR)
    /* 标准 8052 离线适配：12T Timer0 模式 1，ISR 重装计数。
     * 只验证应用状态机；此路径不代表 STC15 外设计时验证。 */
    AUXR = 0;
    TMOD = (TMOD & 0xf0) | 1;
    TH0 = 0xc0; TL0 = 0;
#else
    /* 用户提供的 1ms 基准：STC15 Timer0 1T、模式 0 自动重装。 */
    TR0 = 0;
    AUXR |= 0x80;
    TMOD &= 0xf0;
    TL0 = 0x20; TH0 = 0xd1;
#endif
    TF0 = 0; ET0 = 1; TR0 = 1;
}
static void tick_ack(void)
{
#if defined(RT51_ACCEPTANCE_SIMULATOR)
    TH0 = 0xc0; TL0 = 0;
#endif
    /* 保存完任务上下文后调用，EA=0；真实 ISR 中验证 FromISR 唤醒。
     * 一次最多一个信号量操作，不调用串口、不阻塞、不自行切换任务。 */
    if (phase == 1 && ++isr_divider == 100) {
        isr_divider = 0;
        if (rt51_sem_give_from_isr(&pulse) == RT51_OK) ++isr_pulses;
    }
}
RT51_TICK_ISR(system_tick, 1)

static void expect(uint8_t condition, uint8_t error)
{
    if (!condition && !acceptance_failure) acceptance_failure = error;
}
static void uart_init(void)
{
#if !defined(RT51_ACCEPTANCE_SIMULATOR)
    P_SW1 &= 0x3f;
    P3M1 = (P3M1 & 0xfd) | 1;
    P3M0 = (P3M0 & 0xfe) | 2;
    SCON = 0x50;
    ES = 0;
    /* Timer2 1T，UART1 8N1：12MHz/(4*26)=115384.6 baud。
     * 不开 Timer2 中断，避免与系统 Timer0 共用中断上下文。 */
    AUXR = (AUXR & 0xe3) | 4;
    T2L = 0xe6; T2H = 0xff;
    AUXR |= 0x11;
    TI = RI = 0;
#endif
}
static uint8_t uart_byte(uint8_t value)
{
#if defined(RT51_ACCEPTANCE_SIMULATOR)
    (void)value;
    return 1;
#else
    uint16_t began = rt51_ticks();
    TI = 0;
    SBUF = value;
    while (!TI) {
        if ((uint16_t)(rt51_ticks() - began) >= 3) {
            ++uart_timeouts;
            return 0;
        }
    }
    TI = 0;
    return 1;
#endif
}
static void put16(uint8_t at, uint16_t value)
{
    wire[at] = (uint8_t)value;
    wire[at+1] = (uint8_t)(value >> 8);
}
static void software_timer_callback(void __xdata *context)
{
    /* 在 consumer 任务中派发，既验证回调上下文，也验证禁止阻塞/重入的门禁。 */
    if (!EA || rt51_current != ids[0] || rt51_delay(1) != RT51_CONTEXT || rt51_timer_dispatch() != RT51_BUSY) {
        ++callback_context_errors;
    }
    if (context == &timer_callbacks) ++timer_callbacks;
    else if (context == &one_shot_callbacks) ++one_shot_callbacks;
    else ++callback_context_errors;
}

static void check_snapshots(void)
{
    uint8_t i, ea = rt51_critical_enter();
    expect(rt51_kernel_get_info(&kernel_info) == RT51_OK, 50);
    tick_sample_sum = dispatch_sample_sum = 0;
    for (i = 0; i < RT51_MAX_TASKS; ++i) {
        expect(rt51_task_get_info(i, &task_info) == RT51_OK && task_info.stack_peak <= RT51_STACK_BYTES, 51);
        tick_sample_sum += task_info.tick_samples;
        dispatch_sample_sum += task_info.dispatch_count;
    }
    expect(tick_sample_sum == (uint16_t)(kernel_info.ticks - 0xfff0u) && dispatch_sample_sum == kernel_info.switches, 52);
    expect(!kernel_info.fault && kernel_info.kernel_stack_peak < RT51_KERNEL_STACK_BYTES, 53);
    ++snapshot_checks;
    acceptance_flags |= CHECK_SNAPSHOT;
    rt51_critical_exit(ea);
}
static void report(uint16_t sequence)
{
    uint8_t i, checksum=0, ea;
    wire[0]='R'; wire[1]='5'; wire[2]='1'; wire[3]=0xa5;
    wire[4]=3; wire[5]=80;
    /* 多字节计数来自不同任务/ISR；在一个短临界区内形成完整快照。 */
    ea = rt51_critical_enter();
    put16(6, acceptance_flags);
    wire[8]=acceptance_failure; wire[9]=rt51_fault;
    put16(10, rt51_tick_count); put16(12, rt51_switch_count);
    put16(14, sequence); put16(16, consumed); put16(18, produced);
    wire[20]=(uint8_t)hog_iterations; wire[21]=(uint8_t)(hog_iterations>>8);
    wire[22]=(uint8_t)(hog_iterations>>16); wire[23]=(uint8_t)(hog_iterations>>24);
    put16(24, arithmetic_errors); put16(26, isr_pulses); put16(28, isr_wakes);
    put16(30, timeouts); put16(32, shared_value);
    for(i=0;i<4;++i) wire[34+i]=rt51_tasks[i].stack_peak;
    wire[38]=rt51_kernel_stack_peak; wire[39]=rt51_current;
    wire[40]=0; wire[41]=0x1b; wire[42]=0xb7; wire[43]=0;
    put16(44, uart_timeouts);
    put16(60, timer_callbacks); put16(62, one_shot_callbacks);
    put16(64, period_misses); put16(66, suspend_checks);
    put16(68, tick_sample_sum); put16(70, dispatch_sample_sum);
    put16(72, periodic_timer.overruns); wire[74]=periodic_timer.pending;
    wire[75]=callback_context_errors; put16(76, snapshot_checks);
    for(i=0;i<4;++i) {
        wire[48+i]=(uint8_t)(boot_dividend>>(8*i));
        wire[52+i]=(uint8_t)(boot_quotient>>(8*i));
        wire[56+i]=(uint8_t)(boot_remainder>>(8*i));
    }
    rt51_critical_exit(ea);
    wire[46]=wire[47]=0;
    for(i=0;i<78;++i) checksum ^= wire[i];
    wire[78]=checksum; wire[79]=0x5a;
    for(i=0;i<80;++i) if (!uart_byte(wire[i])) return;
    ++reports;
}
static void consumer(void)
{
    uint16_t began, sequence, previous=0;
    uint32_t last_hog=0;
    uint8_t ea;
    while (!low_owns_mutex) rt51_delay(1);
    began = rt51_ticks();
    expect(rt51_delay(20) == RT51_OK, 1);
    expect((uint16_t)(rt51_ticks()-began) >= 20, 2);
    acceptance_flags |= CHECK_DELAY;
    expect(rt51_ticks() < 100, 3);
    acceptance_flags |= CHECK_WRAP;
    /* 中等优先级任务正忙循环；低优先级持锁者必须靠优先级继承释放锁。 */
    expect(rt51_mutex_lock(&mutex, 100) == RT51_OK, 4);
    expect(phase == 2 && rt51_tasks[2].effective_priority == 10, 5);
    expect(rt51_mutex_unlock(&mutex) == RT51_OK, 6);
    acceptance_flags |= CHECK_INHERIT;
    ea=rt51_critical_enter();
    expect(ea && !EA && rt51_delay(1) == RT51_CONTEXT, 7);
    rt51_critical_exit(ea);
    expect(EA, 8);
    acceptance_flags |= CHECK_CRITICAL;
    /* 在不持锁的 READY 状态挂起计算任务，验证停住后计数不变且能继续执行。 */
    expect(rt51_task_suspend(ids[2]) == RT51_OK, 54);
    expect(rt51_task_get_info(ids[2], &task_info) == RT51_OK && task_info.state == RT51_SUSPENDED, 55);
    last_hog = hog_iterations;
    rt51_delay(2);
    expect(hog_iterations == last_hog, 56);
    expect(rt51_task_resume(ids[2]) == RT51_OK, 57);
    ++suspend_checks; acceptance_flags |= CHECK_SUSPEND;
    phase=1;
    for (;;) {
        expect(rt51_queue_receive(&queue, received, 100) == RT51_OK, 10);
        sequence=(uint16_t)received[0] | ((uint16_t)received[1]<<8);
        expect(sequence == (uint16_t)(previous+1) && received[2] == (uint8_t)(received[0]^received[1]^0xa7), 11);
        previous=sequence; ++consumed; acceptance_flags |= CHECK_QUEUE;
        expect(rt51_event_wait(1, RT51_EVENT_ALL, 100, &event_bits) == RT51_OK && event_bits == 1, 12);
        acceptance_flags |= CHECK_EVENT;
        expect(rt51_mutex_lock(&mutex, 100) == RT51_OK, 13);
        expect(shared_value == sequence, 14);
        expect(rt51_mutex_unlock(&mutex) == RT51_OK, 15);
        if (rt51_sem_take(&pulse, 0) == RT51_OK) { ++isr_wakes; acceptance_flags |= CHECK_SEM|CHECK_ISR; }
        expect(rt51_event_wait(0x80, RT51_EVENT_ANY, 2, &event_bits) == RT51_TIMEOUT, 16);
        ++timeouts; acceptance_flags |= CHECK_TIMEOUT;
        ea=rt51_critical_enter();
        if (hog_iterations != last_hog) acceptance_flags |= CHECK_PREEMPT;
        last_hog=hog_iterations;
        if (math_iterations >= 100 && !arithmetic_errors) acceptance_flags |= CHECK_MATH;
        rt51_critical_exit(ea);
        expect(known_dividend/65521UL == 65259UL && known_dividend%65521UL == 43613UL, 17);
        expect(rt51_timer_dispatch() == RT51_OK, 58);
        expect(rt51_timer_get_info(&periodic_timer, &timer_info) == RT51_OK && !timer_info.overruns, 59);
        expect(!callback_context_errors && one_shot_callbacks <= 1, 60);
        if (timer_callbacks && one_shot_callbacks == 1) acceptance_flags |= CHECK_TIMER|CHECK_CALLBACK;
        if (sequence % 25 == 0 || acceptance_failure) { check_snapshots(); report(sequence); }
    }
}
static void producer(void)
{
    uint16_t sequence=0;
    while (!low_owns_mutex) rt51_delay(1);
    rt51_delay(10);
    /* 无 yield 的中等优先级干扰者，用来验证锁继承。 */
    while (!phase) ++produced;
    produced=0;
    next_release = rt51_ticks();
    for (;;) {
        ++sequence;
        expect(rt51_mutex_lock(&mutex, 100) == RT51_OK, 20);
        shared_value=sequence;
        expect(rt51_mutex_unlock(&mutex) == RT51_OK, 21);
        sent[0]=(uint8_t)sequence; sent[1]=(uint8_t)(sequence>>8); sent[2]=sent[0]^sent[1]^0xa7;
        ++produced;
        expect(rt51_queue_send(&queue, sent, 100) == RT51_OK, 22);
        expect(rt51_event_post(ids[0], 1) == RT51_OK, 23);
        {
            rt51_status_t status = rt51_delay_until(&next_release, 20);
            if (status == RT51_TIMEOUT) ++period_misses;
            else expect(status == RT51_OK, 24);
            if (status == RT51_OK) acceptance_flags |= CHECK_PERIOD;
        }
    }
}
static void cpu_hog(void)
{
    uint32_t number=0x12345678UL;
    volatile uint32_t quotient, remainder;
    expect(rt51_mutex_lock(&mutex, 0) == RT51_OK, 30);
    low_owns_mutex=1;
    while (rt51_tasks[2].effective_priority != 1) ++hog_iterations;
    phase=2;
    expect(rt51_mutex_unlock(&mutex) == RT51_OK, 31);
    /* 持续 32 位重入库运算且不主动让出；每次抢占后验证算术与自动变量。 */
    for (;;) {
        quotient=number/65521UL;
        remainder=number%65521UL;
        if (quotient*65521UL+remainder != number || remainder >= 65521UL) ++arithmetic_errors;
        ++hog_iterations;
        ++math_iterations;
        number+=0x10203UL;
    }
}
void main(void)
{
    /* 保留 C 启动初始化的原始值与未抢占时的算术结果。
     * 正式验收不手动修正此变量，必须由板级 XINIT 适配自动初始化成功。 */
    boot_dividend=known_dividend;
    boot_quotient=known_dividend/65521UL;
    boot_remainder=known_dividend%65521UL;
    rt51_init();
    uart_init();
    expect(rt51_queue_init(&queue, storage, sizeof(storage), 2, 3) == RT51_OK, 40);
    expect(rt51_sem_init(&pulse, 0, 1) == RT51_OK, 41);
    expect(rt51_mutex_init(&mutex) == RT51_OK, 42);
    expect(rt51_timer_init(&periodic_timer, software_timer_callback, &timer_callbacks, 50, 1) == RT51_OK, 61);
    expect(rt51_timer_init(&one_shot_timer, software_timer_callback, &one_shot_callbacks, 30, 0) == RT51_OK, 62);
    expect(rt51_timer_start(&periodic_timer, 50) == RT51_OK && rt51_timer_start(&one_shot_timer, 30) == RT51_OK, 63);
    expect(rt51_task_create(consumer, 1, &ids[0]) == RT51_OK, 43);
    expect(rt51_task_create(producer, 5, &ids[1]) == RT51_OK, 44);
    expect(rt51_task_create(cpu_hog, 10, &ids[2]) == RT51_OK, 45);
    /* 立即覆盖 tick 回绕，不必等 65 秒；不是现实墙钟时间戳。 */
    rt51_tick_count=0xfff0;
    if (!acceptance_failure) expect(rt51_start(timer_init, tick_ack) == RT51_OK, 46);
    EA=0;
    for (;;) { }
}
