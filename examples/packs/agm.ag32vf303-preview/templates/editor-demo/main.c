/*
 * MCU StudioX / AG32VF303CCT6 编辑器演示
 *
 * 可以先修改 APP_SAMPLE_PERIOD、注释或函数，按 Ctrl+S 保存，
 * 再重新打开工程检查修改。此模板不使用外部引脚。
 * 下面的数值是内存中生成的演示数据，不是真实传感器读数。
 *
 * 当前临时包用于工程创建和编辑界面测试，未验证固件编译/下载。
 */
#include <stdbool.h>
#include <stdint.h>
#include "system.h"
#include "interrupt.h"

#define APP_SAMPLE_PERIOD  1000u
#define APP_MAGIC          0x41473332u

typedef enum {
    APP_STATE_STARTING = 0,
    APP_STATE_RUNNING,
    APP_STATE_PAUSED
} app_state_t;

typedef struct {
    uint32_t magic;
    uint32_t device_id;
    uint32_t heartbeat;
    uint32_t sample_count;
    int32_t  temperature_x100;
    bool     digital_state;
    app_state_t state;
} app_telemetry_t;

/* 后续接入调试器时，可在变量窗口观察该结构体。 */
volatile app_telemetry_t g_app = {
    .magic = APP_MAGIC,
    .temperature_x100 = 2500,
    .state = APP_STATE_STARTING
};

static void clock_init(void)
{
    /* 只使用内部时钟，不假设板载晶振频率。 */
    SYS->CLK_CNTL &= ~SYS_CLK_SOURCE_MASK;
    SYS->CLK_CNTL &= ~(SYS_CLK_PLL_ON | SYS_CLK_HSE_ON);
}

static int32_t make_demo_sample(uint32_t index)
{
    /* 20.00 ～ 29.99 °C 的锯齿演示值，用整数避免浮点依赖。 */
    return 2000 + (int32_t)(index % 1000u);
}

static void app_update(void)
{
    ++g_app.heartbeat;

    if (g_app.state != APP_STATE_RUNNING) {
        return;
    }

    /* 这是循环计数，不是毫秒；真实周期应由定时器提供。 */
    if ((g_app.heartbeat % APP_SAMPLE_PERIOD) == 0u) {
        ++g_app.sample_count;
        g_app.temperature_x100 = make_demo_sample(g_app.sample_count);
        g_app.digital_state = !g_app.digital_state;
    }
}

int main(void)
{
    INT_DisableIntGlobal();
    clock_init();

    g_app.device_id = SYS_GetDeviceID();
    g_app.state = APP_STATE_RUNNING;

    for (;;) {
        app_update();
        /* TODO: 添加你的应用逻辑。 */
    }
}
