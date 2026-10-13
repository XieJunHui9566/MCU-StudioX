#include <8052.h>
#include "rt51.h"
RT51_TICK_ISR(system_tick, 1)
volatile __xdata uint16_t first, second;
__xdata uint8_t id;
volatile __xdata uint8_t test_result;
volatile __xdata uint8_t check_count;
void test_finish(void) { EA = 0; TR0 = 0; for (;;) {} }
static void timer_init(void)
{
    TMOD = 1; TH0 = 0; TL0 = 0; ET0 = 1; TR0 = 1;
}
static void a(void)
{
    volatile uint16_t check = 0x1234;
    for (;;) {
        ++first;
        if (check != 0x1234) { test_result = 1; test_finish(); }
    }
}
static void b(void)
{
    for (;;) {
        ++second;
        if (rt51_ticks() >= 4) {
            test_result = first && second && rt51_switch_count >= 4 ? 0x5a : 2;
            check_count = 1;
            test_finish();
        }
    }
}
void main(void)
{
    rt51_init();
    rt51_task_create(a, 2, &id);
    rt51_task_create(b, 2, &id);
    rt51_start(timer_init, 0);
}
