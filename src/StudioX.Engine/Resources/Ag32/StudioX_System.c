/* StudioX generated AG32 system support v1. Edit the pin planner / VE, not this file. */
#include "StudioX_System.h"

/* 旧精简包没有 system.c；弱定义也允许用户显式加入原厂完整驱动。 */
__attribute__((weak)) SYS_ClocksTypeDef SYS_Clocks = {
    .HSI_FREQUENCY = STUDIOX_HSI_HZ,
    .HSE_FREQUENCY = STUDIOX_HSE_HZ,
    .PLL_FREQUENCY = STUDIOX_SYSCLK_HZ,
    .EXT_FREQUENCY = BOARD_EXT_FREQUENCY
};

volatile StudioX_SystemResult StudioX_SystemStatus = STUDIOX_SYSTEM_OK;

@@PIN_OBJECTS@@

/* RV32 必须重读高字，防止读取低字时进位导致计时跳变。 */
static uint64_t StudioX_Cycles(void)
{
    uint32_t high, low, check;
    do
    {
        high = read_csr(mcycleh);
        low = read_csr(mcycle);
        check = read_csr(mcycleh);
    } while (high != check);
    return ((uint64_t)high << 32) | low;
}

uint32_t StudioX_SystemClockHz(void)
{
    switch (SYS->CLK_CNTL & SYS_CLK_SOURCE_MASK)
    {
        case SYS_CLK_SOURCE_HSI: return SYS_Clocks.HSI_FREQUENCY;
        case SYS_CLK_SOURCE_HSE: return SYS_Clocks.HSE_FREQUENCY;
        case SYS_CLK_SOURCE_PLL: return SYS_Clocks.PLL_FREQUENCY;
        default: return SYS_Clocks.EXT_FREQUENCY;
    }
}

uint32_t StudioX_PeripheralClockHz(void)
{
    return (uint32_t)((uint64_t)StudioX_SystemClockHz() / ((uint64_t)SYS->PBUS_DIVIDER + 1u));
}

__attribute__((weak)) uint32_t SYS_GetSysClkFreq(void) { return StudioX_SystemClockHz(); }
__attribute__((weak)) uint32_t SYS_GetPclkFreq(void) { return StudioX_PeripheralClockHz(); }

static void StudioX_WaitCycles(uint64_t ticks)
{
    const uint64_t start = StudioX_Cycles();
    /* 无符号差值可跨计数器回绕；不使用软件空循环推测执行时间。 */
    while ((uint64_t)(StudioX_Cycles() - start) < ticks) { }
}

void Delay_us(uint32_t microseconds)
{
    if (microseconds == 0u) { return; }
    /* 先以 64 位乘，再向上取整，非整 MHz 不会因截断造成少等。 */
    StudioX_WaitCycles(((uint64_t)StudioX_SystemClockHz() * microseconds + 999999u) / 1000000u);
}

void Delay_ms(uint32_t milliseconds)
{
    if (milliseconds == 0u) { return; }
    /* 不先计算 ms * 1000，避免长延迟的 32 位乘法溢出。 */
    StudioX_WaitCycles(((uint64_t)StudioX_SystemClockHz() * milliseconds + 999u) / 1000u);
}

void Delay_1ms(void) { Delay_ms(1u); }

#if STUDIOX_CONFIGURE_PLL
static bool StudioX_WaitReady(uint32_t flag)
{
    const uint64_t start = StudioX_Cycles();
    const uint64_t timeout = STUDIOX_HSI_HZ / 10u; /* 在 HSI 上等待，最多 100 ms。 */
    while ((SYS->CLK_CNTL & flag) == 0u)
    {
        if ((uint64_t)(StudioX_Cycles() - start) >= timeout) { return false; }
    }
    return true;
}
#endif

void StudioX_SystemInit(void)
{
#ifdef STUDIOX_FREERTOS
    /* 调度器取得中断所有权，初始化阶段不能触发尚未建立的任务上下文。 */
    INT_DisableIntGlobal();
    INT_Init();
    INT_ClearSoftwareInt();
#endif
    /* 先回 HSI 再关闭 PLL，不能把正在使用的时钟源直接关掉。 */
    SYS->CLK_CNTL &= ~SYS_CLK_SOURCE_MASK;
    SYS->CLK_CNTL &= ~(SYS_CLK_PLL_ON | SYS_CLK_HSE_ON);
    SYS->PBUS_DIVIDER = 0u;
    SYS_Clocks.HSI_FREQUENCY = STUDIOX_HSI_HZ;
    SYS_Clocks.HSE_FREQUENCY = STUDIOX_HSE_HZ;
    SYS_Clocks.PLL_FREQUENCY = STUDIOX_SYSCLK_HZ;
    StudioX_SystemStatus = STUDIOX_SYSTEM_OK;
#if STUDIOX_CONFIGURE_PLL
    /* 与原厂 SYS_SetSclkAuto 相同，先设置 Flash 时钟分频，再提高 CPU 频率。 */
    const uint32_t divider = (STUDIOX_SYSCLK_HZ - 1u) / FLASH_MAX_FREQ;
    SYS->CLK_CNTL = (SYS->CLK_CNTL & ~(SYS_SCLK_DIV_HIGH_MASK | SYS_SCLK_DIV_LOW_MASK | SYS_CLK_HSE_BYP)) |
        SYS_SCLK_DIV_HIGH(divider) | SYS_SCLK_DIV_LOW(divider) | SYS_CLK_HSE_ON;
    if (!StudioX_WaitReady(SYS_CLK_HSE_RDY))
    {
        StudioX_SystemStatus = STUDIOX_SYSTEM_HSE_TIMEOUT;
        SYS->CLK_CNTL &= ~SYS_CLK_HSE_ON;
    }
    else
    {
        SYS->CLK_CNTL |= SYS_CLK_PLL_ON;
        if (!StudioX_WaitReady(SYS_CLK_PLL_RDY))
        {
            StudioX_SystemStatus = STUDIOX_SYSTEM_PLL_TIMEOUT;
            SYS->CLK_CNTL &= ~(SYS_CLK_PLL_ON | SYS_CLK_HSE_ON);
        }
        else
        {
            SYS->PBUS_DIVIDER = STUDIOX_SYSCLK_HZ / STUDIOX_BUSCLK_HZ - 1u;
            SYS->CLK_CNTL = (SYS->CLK_CNTL & ~SYS_CLK_SOURCE_MASK) | SYS_CLK_SOURCE_PLL;
        }
    }
#endif
@@PIN_INIT@@
#ifdef STUDIOX_AG32_LOGIC_LOOPBACK
    /* 混合模板的内部回环由系统层初始化；不驱动外部封装脚。 */
    SYS_EnableAPBClock(APB_MASK_GPIO4);
    GPIO_SetSoftwareMode(GPIO4, GPIO_BIT0 | GPIO_BIT1);
    GPIO_SetLow(GPIO4, GPIO_BIT0);
    GPIO_SetOutput(GPIO4, GPIO_BIT0);
    GPIO_SetInput(GPIO4, GPIO_BIT1);
#endif
}

#ifdef STUDIOX_FREERTOS
/* 异常位置保留在内存中，便于调试器定位；失败后禁止带错继续调度。 */
volatile uint32_t StudioX_RtosFault;
const char * volatile StudioX_RtosFaultFile;
volatile uint32_t StudioX_RtosFaultLine;

void StudioX_RtosAssert(const char *file, uint32_t line)
{
    INT_DisableIntGlobal();
    StudioX_RtosFault = 1u;
    StudioX_RtosFaultFile = file;
    StudioX_RtosFaultLine = line;
    for (;;) { }
}

void vApplicationMallocFailedHook(void)
{
    StudioX_RtosAssert("FreeRTOS heap exhausted", 0u);
}

void vApplicationStackOverflowHook(TaskHandle_t task, char *name)
{
    (void)task;
    StudioX_RtosAssert(name, 0u);
}

#if configSUPPORT_STATIC_ALLOCATION
void vApplicationGetIdleTaskMemory(StaticTask_t **task, StackType_t **stack, uint32_t *depth)
{
    static StaticTask_t idle_task;
    static StackType_t idle_stack[configMINIMAL_STACK_SIZE];
    *task = &idle_task;
    *stack = idle_stack;
    *depth = configMINIMAL_STACK_SIZE;
}
#if configUSE_TIMERS
void vApplicationGetTimerTaskMemory(StaticTask_t **task, StackType_t **stack, uint32_t *depth)
{
    static StaticTask_t timer_task;
    static StackType_t timer_stack[configTIMER_TASK_STACK_DEPTH];
    *task = &timer_task;
    *stack = timer_stack;
    *depth = configTIMER_TASK_STACK_DEPTH;
}
#endif
#endif

/* 单核 AG32 的 MTIME 随系统时钟计数；在 PLL 成功或 HSI 回退后取实际频率。
 * 移植层保留该变量供汇编节拍入口读取，调度后不要直接切换系统时钟。 */
void vPortSetupTimerInterrupt(void)
{
    extern size_t uxTimerIncrementsForOneTick;
    extern uint64_t ullNextTime;
    extern volatile uint64_t *pullMachineTimerCompareRegister;
    uint32_t high, low, check;
    uxTimerIncrementsForOneTick = StudioX_SystemClockHz() / configTICK_RATE_HZ;
    configASSERT(uxTimerIncrementsForOneTick != 0u);
    pullMachineTimerCompareRegister = (volatile uint64_t *)&CLINT->MTIMECMP_LO;
    do
    {
        high = CLINT->MTIME_HI;
        low = CLINT->MTIME_LO;
        check = CLINT->MTIME_HI;
    } while (high != check);
    ullNextTime = (((uint64_t)high << 32) | low) + uxTimerIncrementsForOneTick;
    INT_SetMtimeCmp(ullNextTime);
    ullNextTime += uxTimerIncrementsForOneTick;
}

/* 不能依赖两个同名弱 handle_trap 的链接顺序；外设中断显式转给 AGM 分派表。
 * 此入口不嵌套；ISR 使用 FromISR API，并按 portYIELD_FROM_ISR 请求调度。 */
void StudioX_FreeRtosInterruptHandler(uintptr_t cause, uintptr_t pc)
{
    handle_trap_nonest(cause, pc, NULL);
}
#endif

/* 已锁定的 AGM 启动代码在 main 前遍历 .init_array，不在主函数堆放初始化。 */
__attribute__((constructor(101))) static void StudioX_BeforeMain(void)
{
    StudioX_SystemInit();
}
