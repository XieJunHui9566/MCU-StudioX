#include "pico/stdlib.h"
#include "pico/multicore.h"
#include "pico/util/queue.h"

static queue_t requests;
static queue_t responses;
volatile uint32_t app_counter;
volatile uint32_t core1_result;
volatile uint32_t response_timeouts;

static void core1_entry(void)
{
    for (;;)
    {
        uint32_t input;
        if (queue_try_remove(&requests, &input))
        {
            uint32_t result = input * 3u + 1u;
            // 核 0 每次只允许一个未完成请求，响应队列不会被第二个结果占用。
            queue_add_blocking(&responses, &result);
        }
        tight_loop_contents();
    }
}

int main(void)
{
    // SDK 队列负责跨核同步；供调试观察的 volatile 变量仅由核 0 写入。
    queue_init(&requests, sizeof(uint32_t), 1);
    queue_init(&responses, sizeof(uint32_t), 1);
    multicore_launch_core1(core1_entry);
    bool pending = false;
    absolute_time_t deadline = nil_time;
    for (;;)
    {
        if (!pending)
        {
            uint32_t input = app_counter + 1u;
            if (queue_try_add(&requests, &input))
            {
                app_counter = input;
                pending = true;
                deadline = make_timeout_time_ms(1000);
            }
        }
        uint32_t result;
        if (pending && queue_try_remove(&responses, &result))
        {
            core1_result = result;
            pending = false;
        }
        else if (pending && time_reached(deadline))
        {
            // 超时保持原请求，避免迟到结果与新请求混淆，并保留可观察计数。
            ++response_timeouts;
            deadline = make_timeout_time_ms(1000);
        }
        sleep_ms(100);
    }
}
